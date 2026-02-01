// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.SdkChat.Helpers;

/// <summary>
/// Thread-safe tracker for cumulative prompt character usage.
/// Enforces budget limits during streaming prompt construction.
/// </summary>
public sealed class PromptBudgetTracker
{
    private readonly int _totalBudget;
    private readonly int _reservedForSystemPrompt;
    private int _consumed;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new budget tracker.
    /// </summary>
    /// <param name="totalBudget">Total character budget for the entire prompt.</param>
    /// <param name="reservedForSystemPrompt">Characters to reserve for system prompt and overhead.</param>
    public PromptBudgetTracker(int totalBudget, int reservedForSystemPrompt = 0)
    {
        if (totalBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBudget), "Budget must be positive");
        if (reservedForSystemPrompt < 0)
            throw new ArgumentOutOfRangeException(nameof(reservedForSystemPrompt), "Reserve cannot be negative");
        if (reservedForSystemPrompt >= totalBudget)
            throw new ArgumentException("Reserve must be less than total budget", nameof(reservedForSystemPrompt));

        _totalBudget = totalBudget;
        _reservedForSystemPrompt = reservedForSystemPrompt;
        _consumed = 0;
    }

    /// <summary>
    /// Total budget in characters.
    /// </summary>
    public int TotalBudget => _totalBudget;

    /// <summary>
    /// Budget available for user prompt content (total minus reserve).
    /// </summary>
    public int AvailableBudget => _totalBudget - _reservedForSystemPrompt;

    /// <summary>
    /// Characters consumed so far.
    /// </summary>
    public int Consumed
    {
        get { lock (_lock) return _consumed; }
    }

    /// <summary>
    /// Remaining characters available for content.
    /// </summary>
    public int Remaining
    {
        get { lock (_lock) return Math.Max(0, AvailableBudget - _consumed); }
    }

    /// <summary>
    /// Whether budget has been exhausted.
    /// </summary>
    public bool IsExhausted
    {
        get { lock (_lock) return _consumed >= AvailableBudget; }
    }

    /// <summary>
    /// Tries to consume the specified number of characters.
    /// Returns the actual number consumed (may be less if budget is exceeded).
    /// </summary>
    /// <param name="chars">Number of characters to consume.</param>
    /// <returns>Actual characters consumed (0 to chars).</returns>
    public int TryConsume(int chars)
    {
        if (chars <= 0) return 0;

        lock (_lock)
        {
            var remaining = AvailableBudget - _consumed;
            if (remaining <= 0) return 0;

            var actual = Math.Min(chars, remaining);
            _consumed += actual;
            return actual;
        }
    }

    /// <summary>
    /// Checks if the specified content would fit within remaining budget.
    /// Does not consume anything.
    /// </summary>
    public bool WouldFit(int chars)
    {
        if (chars <= 0) return true;
        lock (_lock) return chars <= (AvailableBudget - _consumed);
    }

    /// <summary>
    /// Checks if the specified content would fit, with buffer room.
    /// </summary>
    public bool WouldFitWithBuffer(int chars, int buffer)
    {
        if (chars <= 0) return true;
        lock (_lock) return (chars + buffer) <= (AvailableBudget - _consumed);
    }

    /// <summary>
    /// Truncates content to fit within remaining budget.
    /// Returns the truncated content and consumes the budget.
    /// </summary>
    /// <param name="content">Content to potentially truncate.</param>
    /// <param name="wasTruncated">True if content was truncated.</param>
    /// <returns>Original or truncated content.</returns>
    public string ConsumeWithTruncation(string content, out bool wasTruncated)
    {
        if (string.IsNullOrEmpty(content))
        {
            wasTruncated = false;
            return content;
        }

        lock (_lock)
        {
            var remaining = AvailableBudget - _consumed;
            if (remaining <= 0)
            {
                wasTruncated = true;
                return string.Empty;
            }

            if (content.Length <= remaining)
            {
                _consumed += content.Length;
                wasTruncated = false;
                return content;
            }

            // Truncate to fit
            _consumed += remaining;
            wasTruncated = true;
            return content[..remaining];
        }
    }

    /// <summary>
    /// Gets a summary of budget usage for diagnostics.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            var pct = AvailableBudget > 0 ? (100.0 * _consumed / AvailableBudget) : 100.0;
            return $"{_consumed:N0}/{AvailableBudget:N0} chars ({pct:F1}% used)";
        }
    }
}
