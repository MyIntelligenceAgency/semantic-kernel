﻿// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Orchestration;

namespace Microsoft.SemanticKernel.Connectors.AI.Oobabooga.TextCompletion;

/// <summary>
/// Oobabooga implementation of <see cref="ITextStreamingResult"/>. Actual response object is stored in a ModelResult instance, and completion text is simply passed forward.
/// </summary>
internal sealed class TextCompletionStreamingResult : ITextStreamingResult
{
    private readonly ModelResult _responseData;

    public TextCompletionStreamingResult(TextCompletionStreamingResponse responseData)
    {
        this._responseData = new ModelResult(responseData);
    }

    public ModelResult ModelResult => this._responseData;

    public Task<string> GetCompletionAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(this._responseData.GetResult<TextCompletionStreamingResponse>().Text ?? string.Empty);
    }

    public async IAsyncEnumerable<string> GetCompletionStreamingAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await this.GetCompletionAsync(cancellationToken).ConfigureAwait(false);
    }
}
