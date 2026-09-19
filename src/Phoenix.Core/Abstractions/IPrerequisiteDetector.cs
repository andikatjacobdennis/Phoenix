using Phoenix.Core.Configuration;
using Phoenix.Core.Models;

namespace Phoenix.Core.Abstractions;

/// <summary>
/// Finds out whether one prerequisite is present, and which version.
///
/// Detectors are registered by strategy, so a company can add its own (a licence file, an
/// internal agent) without touching any other part of Phoenix.
/// </summary>
public interface IPrerequisiteDetector
{
    DetectionStrategy Strategy { get; }

    Task<DetectionResult> DetectAsync(PrerequisiteDefinition definition, CancellationToken cancellationToken);
}
