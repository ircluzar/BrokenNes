using System;
using System.Collections.Generic;
using System.Linq;

namespace BrokenNes;

/// <summary>
/// Simple application-wide status / message aggregator service.
/// Components can inject this service to publish short status strings
/// (e.g. emulator state, load progress) and subscribe to change events.
/// </summary>
public class StatusService
{
	private readonly object _lock = new();
	private readonly Queue<string> _messages = new();

	/// <summary>The last status message pushed.</summary>
	public string? LastMessage { get; private set; }

	/// <summary>Raised after the list of messages changes.</summary>
	public event Action? StatusChanged;

	/// <summary>Returns a snapshot copy of all stored messages (oldest -> newest).</summary>
	public IReadOnlyList<string> AllMessages
	{
		get
		{
			lock (_lock)
			{
				return _messages.ToList();
			}
		}
	}

	/// <summary>
	/// Push a new status message. Keeps only a bounded number (default 200) to avoid unbounded memory use.
	/// </summary>
	public void Set(string message, int maxHistory = 200)
	{
		if (string.IsNullOrWhiteSpace(message)) return;

		lock (_lock)
		{
			LastMessage = message.Trim();
			_messages.Enqueue(LastMessage);
			while (_messages.Count > maxHistory && _messages.Count > 0)
			{
				_messages.Dequeue();
			}
		}
		StatusChanged?.Invoke();
	}

	/// <summary>Clears all stored messages.</summary>
	public void Clear()
	{
		lock (_lock)
		{
			_messages.Clear();
			LastMessage = null;
		}
		StatusChanged?.Invoke();
	}
}

