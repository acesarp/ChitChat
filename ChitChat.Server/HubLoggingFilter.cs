using Microsoft.AspNetCore.SignalR;

namespace ChitChat.Server;

/// <summary>
/// Logs failed hub calls with who made them, telling expected rejections (HubException: rate limit, validation)<br />
/// apart from real failures. Replaces SignalR's own dispatcher logging, which
/// reports both as errors with no user attached
/// </summary>
public class HubLoggingFilter(ILogger<ChatHub> logger) : IHubFilter {
	public async ValueTask<object?> InvokeMethodAsync(
		HubInvocationContext invocationContext,
		Func<HubInvocationContext, ValueTask<object?>> next) {
		try {
			return await next(invocationContext);
		}
		catch (HubException ex) {
			// Expected rejections ("too fast", "join first") -- the caller already gets the message.
			logger.LogWarning("{HubMethod} by {UserName} rejected: {Reason}",
				invocationContext.HubMethodName, ChatHub.UserNameOf(invocationContext.Context), ex.Message);
			throw;
		}
		catch (Exception ex) {
			logger.LogError(ex, "{HubMethod} by {UserName} failed", invocationContext.HubMethodName, ChatHub.UserNameOf(invocationContext.Context));
			throw;
		}
	}
}
