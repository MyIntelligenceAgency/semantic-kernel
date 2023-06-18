﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.AI;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Diagnostics;

namespace Microsoft.SemanticKernel.Connectors.AI.Oobabooga.TextCompletion;

/// <summary>
/// Oobabooga text completion service API.
/// Adapted from https://github.com/oobabooga/text-generation-webui/tree/main/api-examples
/// </summary>
public sealed class OobaboogaTextCompletion : ITextCompletion
{
    public const string HttpUserAgent = "Microsoft-Semantic-Kernel";
    public const string BlockingUriPath = "/api/v1/generate";
    private const string StreamingUriPath = "/api/v1/stream";
    private const string ResponseObjectTextStreamEvent = "text_stream";
    private const string ResponseObjectStreamEndEvent = "stream_end";

    private readonly Uri _endpoint;
    private readonly int _blockingPort;
    private readonly int _streamingPort;
    private readonly HttpClient _httpClient;
    private readonly ClientWebSocket? _webSocket;

    /// <summary>
    /// Initializes a new instance of the <see cref="OobaboogaTextCompletion"/> class.
    /// </summary>
    /// <param name="endpoint">Endpoint for service API call.</param>
    /// <param name="blockingPort">The port for blocking requests.</param>
    /// <param name="streamingPort">The port for streaming requests.</param>
    /// <param name="httpClient">The HTTP client to use for making regular blocking API requests. If not specified, a default client will be used.</param>
    /// <param name="webSocket">The client web socket to use for making streaming API requests. If not specified, a transient client will be used on each call.</param>
    public OobaboogaTextCompletion(Uri endpoint, int blockingPort, int streamingPort, HttpClient? httpClient = null, ClientWebSocket? webSocket = null)
    {
        Verify.NotNull(endpoint);

        this._endpoint = endpoint;
        this._blockingPort = blockingPort;
        this._streamingPort = streamingPort;
        this._httpClient = httpClient ?? new HttpClient(NonDisposableHttpClientHandler.Instance, disposeHandler: false);
        if (webSocket != null)
        {
            this.SetWebSocketOptions(webSocket);
            this._webSocket = webSocket;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ITextStreamingResult> GetStreamingCompletionsAsync(
        string text,
        CompleteRequestSettings requestSettings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        UriBuilder streamingUri = new(this._endpoint)
        {
            Port = this._streamingPort,
            Path = StreamingUriPath
        };
        if (streamingUri.Uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            streamingUri.Scheme = (streamingUri.Scheme == "https") ? "wss" : "ws";
        }

        var completionRequest = this.CreateOobaboogaRequest(text, requestSettings);

        var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(completionRequest));
        ClientWebSocket? transientClientWebSocket = null;
        try
        {
            ClientWebSocket? clientWebSocket;
            if (this._webSocket is null)
            {
                transientClientWebSocket = new();
                this.SetWebSocketOptions(transientClientWebSocket);
                clientWebSocket = transientClientWebSocket;
            }
            else
            {
                clientWebSocket = this._webSocket;
            }

            await clientWebSocket.ConnectAsync(streamingUri.Uri, cancellationToken).ConfigureAwait(false);

            var sendSegment = new ArraySegment<byte>(requestBytes);
            await clientWebSocket.SendAsync(sendSegment, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[8192]; // Increased buffer size
            MemoryStream messageStream = new();
            while (true)
            {
                WebSocketReceiveResult result;
                do
                {
                    var segment = new ArraySegment<byte>(buffer);
                    result = await clientWebSocket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);
                    await messageStream.WriteAsync(buffer, 0, result.Count, cancellationToken).ConfigureAwait(false);
                } while (!result.EndOfMessage);

                messageStream.Seek(0, SeekOrigin.Begin);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    using var reader = new StreamReader(messageStream, Encoding.UTF8);
                    var messageText = await reader.ReadToEndAsync().ConfigureAwait(false);
                    var responseObject = JsonSerializer.Deserialize<TextCompletionStreamingResponse>(messageText);

                    if (responseObject is null)
                    {
                        throw new AIException(AIException.ErrorCodes.InvalidResponseContent, "Unexpected response from model")
                        {
                            Data = { { "ModelResponse", messageText } },
                        };
                    }

                    switch (responseObject.Event)
                    {
                        case ResponseObjectTextStreamEvent:
                            yield return new TextCompletionStreamingResult(responseObject);
                            break;
                        case ResponseObjectStreamEndEvent:
                            await clientWebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
                            break;
                        default:
                            break;
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    await clientWebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
                }

                messageStream.SetLength(0);

                if (clientWebSocket.State != WebSocketState.Open)
                {
                    break;
                }
            }
        }
        finally
        {
            transientClientWebSocket?.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ITextResult>> GetCompletionsAsync(
        string text,
        CompleteRequestSettings requestSettings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var blockingUri = new UriBuilder(this._endpoint)
            {
                Port = this._blockingPort,
                Path = BlockingUriPath
            };

            var completionRequest = this.CreateOobaboogaRequest(text, requestSettings);

            using var stringContent = new StringContent(
                JsonSerializer.Serialize(completionRequest),
                Encoding.UTF8,
                "application/json");

            using var httpRequestMessage = new HttpRequestMessage()
            {
                Method = HttpMethod.Post,
                RequestUri = blockingUri.Uri,
                Content = stringContent
            };
            httpRequestMessage.Headers.Add("User-Agent", HttpUserAgent);

            using var response = await this._httpClient.SendAsync(httpRequestMessage, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            TextCompletionResponse? completionResponse = JsonSerializer.Deserialize<TextCompletionResponse>(body);

            if (completionResponse is null)
            {
                throw new AIException(AIException.ErrorCodes.InvalidResponseContent, "Unexpected response from model")
                {
                    Data = { { "ModelResponse", body } },
                };
            }

            return completionResponse.Results.Select(completionText => new TextCompletionResult(completionText)).ToList();
        }
        catch (Exception e) when (e is not AIException && !e.IsCriticalException())
        {
            throw new AIException(
                AIException.ErrorCodes.UnknownError,
                $"Something went wrong: {e.Message}", e);
        }
    }

    #region private ================================================================================

    /// <summary>
    /// Creates an Oobabooga request.
    /// </summary>
    /// <param name="text">The text to complete.</param>
    /// <param name="requestSettings">The request settings.</param>
    /// <returns>An OobaboogaTextCompletionRequest.</returns>
    private TextCompletionRequest CreateOobaboogaRequest(string text, CompleteRequestSettings requestSettings)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentNullException(nameof(text));
        }

        // Prepare the request using the provided parameters.
        return new TextCompletionRequest()
        {
            Prompt = text,
            MaxNewTokens = requestSettings.MaxTokens,
            Temperature = requestSettings.Temperature,
            TopP = requestSettings.TopP,
            RepetitionPenalty = GetRepetitionPenalty(requestSettings),
            StoppingStrings = requestSettings.StopSequences.ToList()
        };
    }

    /// <summary>
    /// Sets the options for the <paramref name="clientWebSocket"/>, either persistent and provided by the ctor, or transient if none provided.
    /// </summary>
    private void SetWebSocketOptions(ClientWebSocket clientWebSocket)
    {
        clientWebSocket.Options.SetRequestHeader("User-Agent", HttpUserAgent);
    }

    /// <summary>
    /// Converts the semantic-kernel presence penalty, scaled -2:+2 with default 0 for no penalty to the Oobabooga repetition penalty, strictly positive with default 1 for no penalty. See https://github.com/oobabooga/text-generation-webui/blob/main/docs/Generation-parameters.md and subsequent links for more details.
    /// </summary>
    private static double GetRepetitionPenalty(CompleteRequestSettings requestSettings)
    {
        return 1 + requestSettings.PresencePenalty / 2;
    }

    #endregion
}
