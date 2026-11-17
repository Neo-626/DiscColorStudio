// Adapted from DiscImageStudio v1.4.0: retained analytic DVD streaming only.
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>
/// A controllable payload word's centre in drawing coordinates, plus its actual
/// 16-channel-bit slot and unwrapped spiral angle. Non-payload slots are retained
/// in GlobalCodewordIndex; SYNC occupies two such slots.
/// </summary>
public readonly record struct DvdCodewordSample(
    double Xmm, double Ymm, ulong GlobalCodewordIndex, double TrackAngleRadians);

public sealed record FastDvdStreamProgress(
    int CompletedBlocks,
    int TotalBlocks,
    uint BlockLba,
    long BytesWritten,
    long TotalBytes,
    TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)BytesWritten / TotalBytes;
}

public sealed record FastDvdStreamSummary(
    int CompletedBlocks,
    uint SectorsWritten,
    long BytesWritten,
    long ControlledWords,
    TimeSpan Elapsed);

public static class FastDispersionImageWriter
{
    private static readonly int[] PayloadChannelCenters = BuildPayloadChannelCenters();
    public static FastDvdStreamSummary WriteScrambledCodewordPatternStream(
        Func<DvdCodewordSample, byte> sampleScrambledByte,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<uint,byte[]>? finishBlock = null)
    {
        ArgumentNullException.ThrowIfNull(sampleScrambledByte);
        ValidateStream(output, options);
        PayloadImageSampler sampler = new(
            sampleScrambledByte, options.ImageMapping) { FinishBlock=finishBlock };
        return WriteSampledStream(sampler, output, options, progress, cancellationToken);
    }

    private static void ValidateStream(Stream output, MultiBlockSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("DVD output stream must be writable.", nameof(output));
        }

        Validate(options);
        if (options.PreserveExistingIso)
        {
            throw new ArgumentException(
                "Direct DVD streaming cannot preserve or seek within an existing ISO.",
                nameof(options));
        }
    }

    private static FastDvdStreamSummary WriteSampledStream(
        PayloadImageSampler sampler,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress,
        CancellationToken cancellationToken)
        => WriteBlockStream((index, buffer, token) => GenerateBlock(index, sampler, options, buffer, token),
            output, options, progress, cancellationToken);

    private static FastDvdStreamSummary WriteBlockStream(
        Func<int, byte[], CancellationToken, GeneratedBlock> generateBlock,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress,
        CancellationToken cancellationToken)
    {
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        int parallelism = options.FastParallelism == 0
            ? Environment.ProcessorCount
            : options.FastParallelism;
        int workerCount = Math.Min(totalBlocks, Math.Max(1, parallelism));
        int capacity = Math.Min(totalBlocks, Math.Max(1, checked(workerCount * 2)));
        GeneratedBlock[] generated = CreateBatchBuffers(capacity);
        long totalBytes = checked((long)options.FillSectors * DvdEccBlockEncoder.PayloadBytesPerSector);
        long bytesWritten = 0;
        long controlledWords = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        void Commit(int index, GeneratedBlock block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(block.Payloads);
            bytesWritten = checked(bytesWritten + block.Payloads.Length);
            controlledWords += block.ControlledWords;
            progress?.Invoke(new FastDvdStreamProgress(index + 1, totalBlocks, block.Lba,
                bytesWritten, totalBytes, stopwatch.Elapsed));
        }
        if (workerCount <= 1)
        {
            for (int index = 0; index < totalBlocks; index++)
                Commit(index, generateBlock(index, generated[0].Payloads, cancellationToken));
        }
        else
        {
            // At most 2*workers blocks are live. A slot is reused only after the
            // ordered writer consumes it; slow blocks no longer stop a whole batch.
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var jobs = new BlockingCollection<int>(capacity);
            var ready = Enumerable.Range(0, capacity).Select(_ => new ManualResetEventSlim()).ToArray();
            for (int i = 0; i < capacity; i++) jobs.Add(i);
            ExceptionDispatchInfo? failure = null;
            var workers = new Task[workerCount];
            try
            {
                for (int n = 0; n < workerCount; n++)
                    workers[n] = Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            foreach (int index in jobs.GetConsumingEnumerable(stopped.Token))
                            {
                                int slot = index % capacity;
                                generated[slot] = generateBlock(index, generated[slot].Payloads, stopped.Token);
                                ready[slot].Set();
                            }
                        }
                        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                            stopped.Cancel();
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                for (int index = 0; index < totalBlocks; index++)
                {
                    int slot = index % capacity;
                    ready[slot].Wait(stopped.Token);
                    Commit(index, generated[slot]);
                    ready[slot].Reset();
                    if (index + capacity < totalBlocks) jobs.Add(index + capacity, stopped.Token);
                }
            }
            catch
            {
                // Preserve worker failures rather than reporting their wake-up
                // cancellation as the cause. I/O/progress exceptions stay intact.
                failure?.Throw();
                throw;
            }
            finally
            {
                stopped.Cancel();
                Task.WaitAll(workers.Where(t => t is not null).ToArray());
                foreach (var signal in ready) signal.Dispose();
            }
            failure?.Throw();
        }

        output.Flush();
        stopwatch.Stop();
        return new FastDvdStreamSummary(
            totalBlocks,
            options.FillSectors,
            bytesWritten,
            controlledWords,
            stopwatch.Elapsed);
    }

    private static GeneratedBlock[] CreateBatchBuffers(int count)
    {
        // Bounded reusable payload slots; the ordered writer owns slot reuse.
        GeneratedBlock[] blocks = new GeneratedBlock[count];
        for (int index = 0; index < blocks.Length; index++)
            blocks[index] = new GeneratedBlock(0,
                new byte[DvdEccBlockEncoder.PayloadBytesPerBlock], 0);
        return blocks;
    }

    private static GeneratedBlock GenerateBlock(
        int blockIndex,
        PayloadImageSampler sampler,
        MultiBlockSolveOptions options,
        byte[] payloads,
        CancellationToken cancellationToken = default)
    {
        uint lba = checked(
            options.StartLba + ((uint)blockIndex * DvdEccBlockEncoder.SectorCount));
        if (options.ZeroInitialPayload)
        {
            // Alpha-excluded positions retain their initial payload, never a
            // prior block's data from this reusable slot.
            Array.Clear(payloads);
        }
        else
        {
            int blockSeed = unchecked(options.RandomSeed + (blockIndex * 1_000_003));
            new Random(blockSeed).NextBytes(payloads);
        }

        int controlledWords = 0;
        ulong blockFirstGlobalBit = (ulong)lba * DvdEccBlockEncoder.ChannelBitsPerSector;
        uint firstPsn = checked(lba + options.PsnOffset);
        bool useSimd = options.UseSimdSampling && sampler.SupportsSimdSampling;
        Span<PayloadTarget> samples = stackalloc PayloadTarget[4];
        for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int payloadOffset = sector * DvdEccBlockEncoder.PayloadBytesPerSector;
            Span<byte> sectorPayload = payloads.AsSpan(
                payloadOffset,
                DvdEccBlockEncoder.PayloadBytesPerSector);
            ReadOnlySpan<byte> scrambler = DvdDataFrameBuilder.PayloadScramblerMask(
                firstPsn + checked((uint)sector));
            long toneIndexBase = checked((((long)lba + sector) * DvdEccBlockEncoder.PayloadBytesPerSector));
            int byteIndex = 0;
            while (byteIndex < sectorPayload.Length)
            {
                int fallbackEnd = sectorPayload.Length;
                if (useSimd)
                {
                    // Every lane keeps its actual centre across header/SYNC/ECC gaps.
                    for (; byteIndex <= fallbackEnd - 4; byteIndex += 4)
                    {
                        sampler.SampleFour(blockFirstGlobalBit,
                            PayloadChannelCenters.AsSpan(payloadOffset + byteIndex, 4), samples,
                            toneIndexBase + byteIndex);
                        for (int lane = 0; lane < 4; lane++)
                        {
                            PayloadTarget sample = samples[lane];
                            if (sample.Alpha < options.ImageMapping.AlphaThreshold) continue;
                            sectorPayload[byteIndex + lane] = (byte)(sample.ScrambledByte ^ scrambler[byteIndex + lane]);
                            controlledWords++;
                        }
                    }
                }
                for (; byteIndex < fallbackEnd; byteIndex++)
                {
                    int payloadIndex = payloadOffset + byteIndex;
                    PayloadTarget sample = sampler.SampleGlobalChannelBit(
                        blockFirstGlobalBit + checked((ulong)PayloadChannelCenters[payloadIndex]),
                        toneIndexBase + byteIndex);
                    if (sample.Alpha < options.ImageMapping.AlphaThreshold) continue;
                    sectorPayload[byteIndex] = (byte)(sample.ScrambledByte ^ scrambler[byteIndex]);
                    controlledWords++;
                }
            }
        }

        // The optional finalizer assembles contiguous carrier strings in scrambled payload space.
        if (sampler.FinishBlock is {} finish)
        {
            for (int sector=0;sector<16;sector++) DvdDataFrameBuilder.TransformPayloadScrambling(payloads.AsSpan(sector*2048,2048),firstPsn+(uint)sector);
            finish(lba,payloads);
            for (int sector=0;sector<16;sector++) DvdDataFrameBuilder.TransformPayloadScrambling(payloads.AsSpan(sector*2048,2048),firstPsn+(uint)sector);
        }
        return new GeneratedBlock(lba, payloads, controlledWords);
    }

    private static int[] BuildPayloadChannelCenters()
    {
        int[] centers = new int[DvdEccBlockEncoder.PayloadBytesPerBlock];
        for (int payloadIndex = 0; payloadIndex < centers.Length; payloadIndex++)
        {
            centers[payloadIndex] = checked(
                DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex) + 8);
        }

        return centers;
    }

    private static void Validate(MultiBlockSolveOptions options)
    {
        if (options.Algorithm != SolverAlgorithm.Dispersion)
        {
            throw new ArgumentException("Fast output requires --algorithm dispersion.");
        }

        if (options.FastParallelism is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Fast parallelism must be 0 (automatic) or 1..64.");
        }

        if (options.FillSectors < DvdEccBlockEncoder.SectorCount
            || options.FillSectors % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Fast output sector count must be a positive multiple of 16.");
        }

        if ((ulong)options.StartLba + options.FillSectors > options.ImageMapping.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Filled range exceeds the disc size.");
        }

        uint firstPsn = checked(options.StartLba + options.PsnOffset);
        uint finalPsn = checked(firstPsn + options.FillSectors - 1);
        if ((firstPsn & 0xF) != 0)
        {
            throw new ArgumentException("The first PSN must be aligned to a 16-sector ECC Block.");
        }

        if (finalPsn > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The output exceeds the 24-bit PSN range.");
        }
    }

    private readonly record struct GeneratedBlock(
        uint Lba,
        byte[] Payloads,
        int ControlledWords);

    private readonly record struct PayloadTarget(byte ScrambledByte, byte Alpha);

    private sealed class PayloadImageSampler
    {
        internal Action<uint,byte[]>? FinishBlock { get; init; }
        private readonly Func<DvdCodewordSample, byte> _sampleScrambledCodeword;
        private readonly ArchimedeanSpiral _spiral;
        private readonly double _bitLengthMm, _startAngleRadians, _direction;
        internal bool SupportsSimdSampling => Vector256.IsHardwareAccelerated && !_spiral.IsConstantPitch;
        internal PayloadImageSampler(Func<DvdCodewordSample, byte> sample, ImageMappingOptions options)
        {
            _sampleScrambledCodeword = sample;
            _bitLengthMm = options.ChannelBitLengthNm * 1e-6;
            double trackLengthMm = options.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector * _bitLengthMm;
            _spiral = ArchimedeanSpiral.Create(options.InnerRadiusMm, options.OuterRadiusMm,
                trackLengthMm, options.PitchLinear, options.PitchQuadratic, options.PitchCubic);
            _startAngleRadians = options.StartAngleDegrees * Math.PI / 180.0;
            _direction = options.Clockwise ? -1.0 : 1.0;
        }
        internal PayloadTarget SampleGlobalChannelBit(ulong globalChannelBit, long toneIndex)
        {
            double arcLengthMm = (globalChannelBit + 0.0) * _bitLengthMm;
            // The shared geometry keeps the constant-pitch Newton shortcut and
            // uses its precomputed inverse curve for a varying radial pitch.
            (double radiusMm, double trackAngle) = _spiral.AtArcLengthFast(arcLengthMm);
            double polarAngle = _startAngleRadians + (_direction * trackAngle);
            return SamplePoint(radiusMm, polarAngle, trackAngle, globalChannelBit, toneIndex);
        }

        private PayloadTarget SamplePoint(double radiusMm, double polarAngle, double trackAngle,
            ulong globalChannelBit, long toneIndex)
        {
            double xMm = radiusMm * Math.Cos(polarAngle);
            double yMm = radiusMm * Math.Sin(polarAngle);
            return new PayloadTarget(_sampleScrambledCodeword(new DvdCodewordSample(
                xMm, -yMm, globalChannelBit / 16, trackAngle)), 255);
        }
        internal void SampleFour(ulong blockFirstGlobalBit, ReadOnlySpan<int> centres,
            Span<PayloadTarget> samples, long toneIndexBase)
        {
            Vector256<double> bits = Vector256.Create(
                (double)(blockFirstGlobalBit + (ulong)centres[0]),
                (double)(blockFirstGlobalBit + (ulong)centres[1]),
                (double)(blockFirstGlobalBit + (ulong)centres[2]),
                (double)(blockFirstGlobalBit + (ulong)centres[3]));
            (Vector256<double> radius, Vector256<double> trackAngle) =
                _spiral.AtArcLengthFast(bits * Vector256.Create(_bitLengthMm));
            Vector256<double> angle = Vector256.Create(_startAngleRadians)
                + Vector256.Create(_direction) * trackAngle;
            for (int lane = 0; lane < 4; lane++)
                samples[lane] = SamplePoint(radius.GetElement(lane), angle.GetElement(lane),
                    trackAngle.GetElement(lane), blockFirstGlobalBit + (ulong)centres[lane], toneIndexBase + lane);
        }
    }
}
