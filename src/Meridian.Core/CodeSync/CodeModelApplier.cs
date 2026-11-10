using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>
/// Applies a <see cref="CodeModel"/> read from source code to a model (a code-sync review branch) and reports what it
/// changed. The code-sync runner depends on this seam rather than on the mapper directly, so tests can use a fake.
/// </summary>
public interface ICodeModelApplier
{
    CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options);
}

/// <summary>The default <see cref="ICodeModelApplier"/>: the code-to-model mapper.</summary>
public sealed class CodeModelApplier : ICodeModelApplier
{
    public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options) =>
        CodeModelMapper.Apply(model, code, options);
}
