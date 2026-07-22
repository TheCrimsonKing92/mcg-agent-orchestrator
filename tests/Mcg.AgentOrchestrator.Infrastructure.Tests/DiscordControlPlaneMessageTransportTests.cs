using System.Net;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordControlPlaneMessageTransportTests
{
    [Xunit.Fact(DisplayName = "DiscordControlPlaneMessageTransport_same_correlation_key_posts_once_then_patches")]
    public async Task DiscordControlPlaneMessageTransportSameCorrelationKeyPostsOnceThenPatches()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"4242"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"4242"}""")
            });
        var transport = new DiscordControlPlaneMessageTransport(
            new HttpClient(handler),
            EnabledOptions());

        var first = await transport.SendOrEditAsync(
            "goal-a:failed-task",
            ControlPlaneDeliveryChannel.Decisions,
            "first",
            [new DiscordButtonDefinition("Resolve", "resolve-1", DiscordButtonStyle.Success)]);
        var second = await transport.SendOrEditAsync(
            "goal-a:failed-task",
            ControlPlaneDeliveryChannel.Decisions,
            "updated",
            []);

        Assert.Equal(4242UL, first);
        Assert.Equal(4242UL, second);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/channels/111/messages", request.Path, StringComparison.Ordinal);
                Assert.Contains("first", request.Body);
                Assert.Contains("custom_id", request.Body);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.EndsWith("/channels/111/messages/4242", request.Path, StringComparison.Ordinal);
                Assert.Contains("updated", request.Body);
            });
    }

    [Xunit.Fact(DisplayName = "DiscordControlPlaneMessageTransport_notifications_disabled_sends_no_http_requests")]
    public async Task DiscordControlPlaneMessageTransportNotificationsDisabledSendsNoHttpRequests()
    {
        var absentFlagHandler = new RecordingHandler();
        var absentFlagTransport = new DiscordControlPlaneMessageTransport(new HttpClient(absentFlagHandler));
        var falseFlagHandler = new RecordingHandler();
        var falseFlagTransport = new DiscordControlPlaneMessageTransport(
            new HttpClient(falseFlagHandler),
            EnabledOptions() with { NotificationsEnabled = false });

        var absentId = await absentFlagTransport.SendOrEditAsync(
            "correlation-a",
            ControlPlaneDeliveryChannel.Decisions,
            "disabled",
            []);
        await falseFlagTransport.SendAsync(ControlPlaneDeliveryChannel.Decisions, "disabled", []);
        await falseFlagTransport.EditAsync(ControlPlaneDeliveryChannel.Decisions, 1, "disabled", []);

        Assert.Equal(0UL, absentId);
        Assert.Empty(absentFlagHandler.Requests);
        Assert.Empty(falseFlagHandler.Requests);
    }

    private static DiscordControlPlaneMessageTransportOptions EnabledOptions() =>
        new()
        {
            NotificationsEnabled = true,
            BotToken = "test-token",
            ApiBaseUri = new Uri("https://discord.test/api/v10/"),
            ChannelIds = new Dictionary<ControlPlaneDeliveryChannel, ulong>
            {
                [ControlPlaneDeliveryChannel.Decisions] = 111,
                [ControlPlaneDeliveryChannel.Board] = 222,
                [ControlPlaneDeliveryChannel.Digest] = 333
            }
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public RecordingHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"1"}""") }
                : _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body);
}
