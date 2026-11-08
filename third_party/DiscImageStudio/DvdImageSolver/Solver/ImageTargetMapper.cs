using DiscImageStudio.Core;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed record ImageMappingOptions(
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm,
    double StartAngleDegrees,
    bool Clockwise,
    byte LuminanceThreshold,
    byte AlphaThreshold,
    int SampleEveryChannelBits = 1,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    ToneProfile? Tone = null);

