using System.Diagnostics;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed record MultiBlockSolveOptions(
    uint StartLba,
    uint FillSectors,
    uint PsnOffset,
    int IterationsPerBlock,
    int MutationBytes,
    int RandomSeed,
    double InitialTemperature,
    double FinalTemperature,
    bool ZeroInitialPayload,
    ImageMappingOptions ImageMapping,
    SolverAlgorithm Algorithm = SolverAlgorithm.Full,
    int BridgeBytes = 2,
    int BeamWidth = 8,
    int RetryBeamWidth = 32,
    double RetryFrameFraction = 0.25,
    int BatchFrames = 8,
    int BatchCandidates = 4,
    int SearchLanes = 1,
    int FastParallelism = 0,
    bool PreserveExistingIso = false,
    bool UseSampleReuse = true)
{
    /// <summary>Use four-double raster sampling in fast output when supported; false retains the scalar reference path.</summary>
    public bool UseSimdSampling { get; init; } = true;
}

