using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CpuEmulator.Generators;

/// <summary>
/// Equatable, tree-free diagnostic carrier for the incremental pipeline. Storing
/// <see cref="Diagnostic"/> in pipeline state roots old syntax trees and defeats caching;
/// this type holds only value data and re-materializes a Diagnostic at report time.
/// </summary>
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    public string DescriptorId { get; }
    public string FilePath { get; }
    public TextSpan Span { get; }
    public LinePositionSpan LineSpan { get; }
    public string[] Args { get; }

    public DiagnosticInfo(DiagnosticDescriptor descriptor, Location location, params string[] args)
    {
        DescriptorId = descriptor.Id;
        FilePath = location.SourceTree?.FilePath ?? location.GetLineSpan().Path ?? string.Empty;
        Span = location.SourceSpan;
        LineSpan = location.GetLineSpan().Span;
        Args = args;
    }

    public Diagnostic ToDiagnostic()
    {
        var descriptor = SpecDiagnostics.ById(DescriptorId);
        // Always re-create via Location.Create so the SourceSpan is preserved even when
        // FilePath is empty (e.g. in-memory source trees in tests have no file path).
        // This gives an external-file location with no SourceTree — callers that need to
        // read the span text must use source.Substring(span.Start, span.Length).
        var location = Location.Create(FilePath, Span, LineSpan);
        // Args are pre-stringified; object[] covariance is fine here.
        return Diagnostic.Create(descriptor, location, Args.Cast<object>().ToArray());
    }

    public bool Equals(DiagnosticInfo? other) =>
        other is not null &&
        DescriptorId == other.DescriptorId &&
        FilePath == other.FilePath &&
        Span == other.Span &&
        Args.SequenceEqual(other.Args);

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = DescriptorId.GetHashCode();
            hash = hash * 31 + FilePath.GetHashCode();
            hash = hash * 31 + Span.GetHashCode();
            foreach (string arg in Args)
                hash = hash * 31 + arg.GetHashCode();
            return hash;
        }
    }
}
