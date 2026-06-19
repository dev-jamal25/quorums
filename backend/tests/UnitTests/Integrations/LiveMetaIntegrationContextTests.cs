using System.Net;
using System.Text;
using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Meta;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.UnitTests.Integrations;

/// <summary>
/// DL-055 live recovery seam: the per-creation publish context must survive a Hangfire retry, which
/// resolves a FRESH <see cref="LiveMetaIntegration"/> (the typed HttpClient client is transient). The
/// context lives in the singleton <see cref="LivePublishContextStore"/>, NOT on the instance — so a new
/// instance sharing the store can poll/publish a container an earlier instance created (e.g. after an
/// Instagram "container still processing" poll). With a per-instance map (the old bug) the retry lost the
/// context and could never publish. No network: a stub handler returns canned Graph responses.
/// </summary>
public sealed class LiveMetaIntegrationContextTests
{
    private static readonly IOptions<MetaOptions> _options = Options.Create(new MetaOptions
    {
        Mode = "live",
        GraphBaseUrl = "https://graph.facebook.com",
        GraphApiVersion = "v21.0",
    });

    private static PublishRequest Request() => new(
        Guid.NewGuid(), PublishChannel.Instagram, "ig-user-123", PostSurface.FeedImage,
        "https://public/img.png", "caption", [], "page-token");

    private static LiveMetaIntegration NewClient(LivePublishContextStore store) =>
        new(new HttpClient(new StubGraphHandler()) { BaseAddress = new Uri("https://graph.facebook.com/") }, _options, store);

    [Fact]
    public async Task Context_survives_across_instances_that_share_the_store()
    {
        var store = new LivePublishContextStore();

        // Instance A creates the container and captures its context in the shared store.
        var created = await NewClient(store).CreateContainerAsync(Request());
        Assert.NotNull(created.CreationId);

        // Instance B is a FRESH client (as a Hangfire retry would resolve) sharing the same store, so it
        // recovers the context and polls the committed container instead of failing ContextMissing.
        var recovered = await NewClient(store).PollContainerAsync(PublishChannel.Instagram, created.CreationId!);
        Assert.True(recovered.Processed);
        Assert.Null(recovered.Failure);
    }

    [Fact]
    public async Task A_separate_store_has_no_context_proving_the_store_carries_it()
    {
        var created = await NewClient(new LivePublishContextStore()).CreateContainerAsync(Request());

        // A fresh instance with a DIFFERENT store (the old per-instance behaviour) cannot recover it.
        var missing = await NewClient(new LivePublishContextStore())
            .PollContainerAsync(PublishChannel.Instagram, created.CreationId!);

        Assert.False(missing.Processed);
        Assert.Equal(PublishStatus.TransientFailure, missing.Failure);
    }

    // Canned Graph responses: POST (create/publish) -> {"id":...}; GET (poll) -> status_code FINISHED.
    private sealed class StubGraphHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.Method == HttpMethod.Post
                ? "{\"id\":\"container-123\"}"
                : "{\"status_code\":\"FINISHED\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
