using Meridian.Assistant.Engine;
using Meridian.Assistant.Providers;
using Meridian.Core.CodeSync;
using Meridian.Web.Services;

namespace Meridian.Web.CodeSync;

/// <summary>Where code sync gets the writer for its optional AI pass (<see cref="Hosts.CodeRepoConfig.AiDescriptions"/>).
/// Tests register a fake; nothing here is called unless a repository turned the pass on.</summary>
public interface ICodeDescriberSource
{
    /// <summary>A describer using the server's AI assistant provider, or null when none is set up or the assistant is off.</summary>
    ICodeDescriber? Create();
}

/// <summary>The AI assistant's configured provider (Claude, a local model or Claude Code), at the server's model.</summary>
public sealed class AssistantCodeDescriberSource(AssistantSettings settings, IAssistantClientFactory clients, ILocalModelHttpFactory localHttp) : ICodeDescriberSource
{
    public ICodeDescriber? Create()
    {
        if (settings.UnavailableReason != null) return null;
        IChatProvider? provider = settings.Provider switch
        {
            AssistantProvider.Local => settings.LocalOptions() is { } o ? new OpenAiCompatibleProvider(localHttp.Create(o.BaseUrl), o) : null,
            AssistantProvider.ClaudeCode => settings.ClaudeCodeOptions() is { } c ? new ClaudeCodeProvider(c) : null,
            _ => settings.ApiKey() is { } key ? new AnthropicChatProvider(clients.Create(key), settings.Options) : null,
        };
        return provider is null ? null : new CodeDescriptionWriter(provider);
    }
}
