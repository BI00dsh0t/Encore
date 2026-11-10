namespace Encore.Core.Library;

/// <summary>How a write the Library was told about ended.</summary>
public enum WriteOutcome
{
    /// <summary>The file is complete at its final path.</summary>
    Written,

    /// <summary>The write failed and its files were removed.</summary>
    Failed,
}
