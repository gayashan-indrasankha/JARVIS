using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tools;

internal sealed class DefaultToolAuthorizationPolicy(IOptions<ToolOptions> options) :
    IToolAuthorizationPolicy
{
    private readonly ToolOptions _options = options.Value;

    public ValueTask<ToolAuthorizationResult> AuthorizeAsync(
        ToolAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ToolAuthorizationResult result = !_options.Enabled
            ? new ToolAuthorizationResult(ToolAuthorizationDecision.Denied, "tools_disabled")
            : request.Category switch
            {
                ToolAuthorizationCategory.SafeRead => new ToolAuthorizationResult(
                    ToolAuthorizationDecision.Allowed,
                    "safe_read_allowed"),
                ToolAuthorizationCategory.SafeLocalAction when _options.AllowSafeLocalActions =>
                    new ToolAuthorizationResult(
                        ToolAuthorizationDecision.Allowed,
                        "safe_local_action_allowed"),
                ToolAuthorizationCategory.SafeLocalAction => new ToolAuthorizationResult(
                    ToolAuthorizationDecision.Denied,
                    "safe_local_actions_disabled"),
                ToolAuthorizationCategory.ConfirmRequired => new ToolAuthorizationResult(
                    ToolAuthorizationDecision.ConfirmationRequired,
                    "confirmation_required"),
                ToolAuthorizationCategory.StrongConfirmRequired => new ToolAuthorizationResult(
                    ToolAuthorizationDecision.StrongConfirmationRequired,
                    "strong_confirmation_required"),
                ToolAuthorizationCategory.Denied => new ToolAuthorizationResult(
                    ToolAuthorizationDecision.Denied,
                    "tool_denied"),
                _ => new ToolAuthorizationResult(
                    ToolAuthorizationDecision.Denied,
                    "authorization_category_invalid"),
            };
        return ValueTask.FromResult(result);
    }
}
