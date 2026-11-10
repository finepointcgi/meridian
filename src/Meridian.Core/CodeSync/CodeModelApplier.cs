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
    // TODO(code-sync merge): delegate to CodeModelMapper.Apply(model, code, options) (Meridian.Core/CodeSync/CodeModelMapper.cs)
    // once the mapper lands; until then a sync records a Failed run that says so.
    public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options) =>
        throw new NotImplementedException("The code-to-model mapper (CodeModelMapper) is not available in this build yet.");
}
