using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Backend.UnitTests.Knowledge;

/// <summary>
/// Slice-3 config surface binds (DL-025): the nested S2 blend weights, plus the reranker and
/// query-transform sections. Tuning knobs are config-bound, never literals.
/// </summary>
public sealed class RetrievalOptionsBindingTests
{
    [Fact]
    public void Retrieval_blend_weights_bind_from_config()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retrieval:Blend:Beta"] = "0.5",
            ["Retrieval:Blend:RecencyHalfLifeDays"] = "14",
        }).Build();

        var opts = cfg.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>()!;

        Assert.Equal(0.5, opts.Blend.Beta);
        Assert.Equal(14.0, opts.Blend.RecencyHalfLifeDays);
        Assert.Equal(1.0, opts.Blend.Alpha);    // default preserved
        Assert.Equal(0.0, opts.Blend.Gamma);    // inert in slice 3
    }

    [Fact]
    public void Blend_defaults_hold_when_section_absent()
    {
        var opts = new ConfigurationBuilder().Build()
            .GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>() ?? new RetrievalOptions();

        Assert.Equal(1.0, opts.Blend.Alpha);
        Assert.Equal(0.3, opts.Blend.Beta);
        Assert.Equal(0.3, opts.Blend.Delta);
        Assert.Equal(30.0, opts.Blend.RecencyHalfLifeDays);
    }

    [Fact]
    public void Reranker_and_query_transform_sections_bind()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reranker:Endpoint"] = "tei-rerank:80",
            ["Reranker:Mode"] = "mock",
            ["QueryTransform:Model"] = "claude-haiku-4-5",
            ["QueryTransform:Mode"] = "mock",
        }).Build();

        var rerank = cfg.GetSection(RerankerOptions.SectionName).Get<RerankerOptions>()!;
        var qt = cfg.GetSection(QueryTransformOptions.SectionName).Get<QueryTransformOptions>()!;

        Assert.Equal("mock", rerank.Mode);
        Assert.Equal("tei-rerank:80", rerank.Endpoint);
        Assert.Equal("claude-haiku-4-5", qt.Model);
        Assert.Equal("mock", qt.Mode);
    }
}
