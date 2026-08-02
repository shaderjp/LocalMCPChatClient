using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class ToolApprovalService : IToolApprovalService
{
    private readonly ISettingsStore _settingsStore;
    private readonly object _sync = new();
    private Dictionary<(string ServerId, string ToolName), ApprovalDecision> _rules;

    public ToolApprovalService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        var settings = settingsStore.LoadAsync().GetAwaiter().GetResult();
        _rules = settings.ApprovalRules.ToDictionary(rule => (rule.ServerId, rule.ToolName), rule => rule.Decision);
    }

    public ApprovalDecision Evaluate(string serverId, string toolName)
    {
        lock (_sync)
        {
            return _rules.GetValueOrDefault((serverId, toolName), ApprovalDecision.Ask);
        }
    }

    public async Task RememberAsync(string serverId, string toolName, ApprovalDecision decision, CancellationToken cancellationToken = default)
    {
        if (decision == ApprovalDecision.Ask) return;
        lock (_sync) _rules[(serverId, toolName)] = decision;

        await _settingsStore.UpdateAsync(settings =>
        {
            var rules = settings.ApprovalRules
                .Where(rule => !(rule.ServerId == serverId && rule.ToolName == toolName))
                .Append(new ToolApprovalRule(serverId, toolName, decision))
                .ToList();
            return settings with { ApprovalRules = rules };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync) _rules.Clear();
        await _settingsStore.UpdateAsync(settings => settings with { ApprovalRules = [] }, cancellationToken).ConfigureAwait(false);
    }
}
