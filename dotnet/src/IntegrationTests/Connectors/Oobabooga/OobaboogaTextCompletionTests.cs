﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Connectors.AI.Oobabooga.TextCompletion;
using Xunit;

namespace SemanticKernel.IntegrationTests.Connectors.Oobabooga;

/// <summary>
/// Integration tests for <see cref=" OobaboogaTextCompletion"/>.
/// </summary>
public sealed class OobaboogaTextCompletionTests
{
    private const string Endpoint = "http://localhost";
    private const int BlockingPort = 5000;
    private const int StreamingPort = 5005;

    private readonly IConfigurationRoot _configuration;

    public OobaboogaTextCompletionTests()
    {
        // Load configuration
        this._configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "testsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile(path: "testsettings.development.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private const string Input = " My name is";

    [Fact(Skip = "This test is for manual verification.")]
    public async Task OobaboogaLocalTextCompletionAsync()
    {
        using var oobaboogaLocal = new OobaboogaTextCompletion(new Uri(Endpoint), BlockingPort, StreamingPort);

        // Act
        var localResponse = await oobaboogaLocal.CompleteAsync(Input, new CompleteRequestSettings()
        {
            Temperature = 0.01,
            MaxTokens = 7,
            TopP = 0.1,
        });

        AssertAcceptableResponse(localResponse);
    }

    [Fact(Skip = "This test is for manual verification.")]
    public void OobaboogaLocalTextCompletionStreamingAsync()
    {
        using var oobaboogaLocal = new OobaboogaTextCompletion(new Uri(Endpoint), BlockingPort, StreamingPort);

        // Act
        var localResponse = oobaboogaLocal.CompleteStreamAsync(Input, new CompleteRequestSettings()
        {
            Temperature = 0.01,
            MaxTokens = 7,
            TopP = 0.1,
        });

        var resultsMerged = localResponse.ToEnumerable().Aggregate((s, s1) => s + s1);
        AssertAcceptableResponse(resultsMerged);
    }

    private static void AssertAcceptableResponse(string localResponse)
    {
        // Assert
        Assert.NotNull(localResponse);
        // Depends on the target LLM obviously, but most LLMs should propose an arbitrary surname preceded by a white space, including the start prompt or not
        // ie "  My name is" => " John (...)" or "  My name is" => " My name is John (...)".
        // Here are a couple LLMs that were tested successfully: gpt2, aisquared_dlite-v1-355m, bigscience_bloomz-560m,  eachadea_vicuna-7b-1.1, TheBloke_WizardLM-30B-GPTQ etc.
        // A few will return an empty string, but well those shouldn't be used for integration tests.
        var expectedRegex = new Regex(@"\s\w+.*");
        Assert.Matches(expectedRegex, localResponse);
    }
}