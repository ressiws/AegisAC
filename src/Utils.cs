
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace AegisAC;

public static class Utils
{
	private static ISwiftlyCore? _core;
	private static ILogger? _logger;
	private static readonly object ConsoleLock = new();

	public static bool DebugMode { get; set; } = false;

	private sealed record ColorStyle(
		ConsoleColor Console,
		string Chat
	);

	private static readonly Dictionary<string, ColorStyle> Colors =
		new(StringComparer.OrdinalIgnoreCase)
		{
			["default"] = new(ConsoleColor.Gray, "default"),
			["white"] = new(ConsoleColor.White, "white"),
			["darkred"] = new(ConsoleColor.DarkRed, "darkred"),
			["red"] = new(ConsoleColor.Red, "red"),
			["lightred"] = new(ConsoleColor.Red, "lightred"),
			["green"] = new(ConsoleColor.Green, "green"),
			["lightgreen"] = new(ConsoleColor.Green, "lime"),
			["slimegreen"] = new(ConsoleColor.Green, "lime"),
			["lime"] = new(ConsoleColor.Green, "lime"),
			["grey"] = new(ConsoleColor.Gray, "grey"),
			["gray"] = new(ConsoleColor.Gray, "grey"),
			["yellow"] = new(ConsoleColor.Yellow, "yellow"),
			["gold"] = new(ConsoleColor.DarkYellow, "gold"),
			["lightblue"] = new(ConsoleColor.Cyan, "lightblue"),
			["blue"] = new(ConsoleColor.Blue, "blue"),
			["darkblue"] = new(ConsoleColor.DarkBlue, "darkblue"),
			["purple"] = new(ConsoleColor.DarkMagenta, "purple"),
			["lightpurple"] = new(ConsoleColor.Magenta, "lightpurple"),
			["pink"] = new(ConsoleColor.Magenta, "lightpurple"),
			["fadedred"] = new(ConsoleColor.DarkRed, "lightred"),
			["orange"] = new(ConsoleColor.DarkYellow, "orange"),
			["olive"] = new(ConsoleColor.DarkYellow, "olive"),
			["silver"] = new(ConsoleColor.Gray, "silver"),
			["bluegrey"] = new(ConsoleColor.DarkCyan, "bluegrey"),
			["magenta"] = new(ConsoleColor.Magenta, "magenta"),
			["invisible"] = new(ConsoleColor.Gray, "default")
		};

	public static void Initialize(ISwiftlyCore? core)
	{
		_core = core;
		_logger = core?.LoggerFactory.CreateLogger("AegisAC");
	}

	public static void Log(string message)
	{
		string formatted = $"{{red}} @AegisAC {{grey}}~ {{default}} {message}";

		WriteConsole(formatted);

		if (!DebugMode || _core is null)
			return;

		string chatMessage = ToChatColors(formatted);
		_core.PlayerManager.SendChat(chatMessage);
	}

	/// <summary>Server console diagnostic log (never broadcast to player chat).</summary>
	public static void ConsoleLog(string message)
	{
		WriteConsole($"{{red}} @AegisAC {{grey}}~ {{default}} {message}");
	}

	private static void WriteConsole(string message)
	{
		lock (ConsoleLock)
		{
			ConsoleColor originalColor = Console.ForegroundColor;

			try
			{
				int position = 0;

				while (position < message.Length)
				{
					if (TryReadColor(message, position, out string color, out int end))
					{
						if (Colors.TryGetValue(color, out ColorStyle? style))
						{
							Console.ForegroundColor = style.Console;
							position = end;
							continue;
						}
					}

					int nextTag = message.IndexOf('{', position);
					if (nextTag < 0)
						nextTag = message.Length;

					Console.Write(message.AsSpan(position, nextTag - position));
					position = nextTag;
				}

				Console.WriteLine();

				_logger?.LogInformation("{Message}", StripColors(message));
			}
			finally
			{
				Console.ForegroundColor = originalColor;
			}
		}
	}

	private static string StripColors(string message)
	{
		var result = new System.Text.StringBuilder(message.Length);
		int position = 0;

		while (position < message.Length)
		{
			if (TryReadColor(message, position, out string color, out int end)
				&& Colors.ContainsKey(color))
			{
				position = end;
				continue;
			}

			result.Append(message[position]);
			position++;
		}

		return result.ToString();
	}

	private static string ToChatColors(string message)
	{
		var result = new System.Text.StringBuilder(message.Length);
		int position = 0;

		while (position < message.Length)
		{
			if (TryReadColor(message, position, out string color, out int end)
				&& Colors.TryGetValue(color, out ColorStyle? style))
			{
				result.Append('[');
				result.Append(style.Chat);
				result.Append(']');
				position = end;
				continue;
			}

			result.Append(message[position]);
			position++;
		}

		return result.ToString();
	}

	private static bool TryReadColor(
		string message,
		int position,
		out string color,
		out int end)
	{
		color = string.Empty;
		end = position;

		if (message[position] != '{')
			return false;

		int closingBrace = message.IndexOf('}', position + 1);

		if (closingBrace < 0)
			return false;

		string candidate = message[(position + 1)..closingBrace];

		if (!Colors.ContainsKey(candidate))
			return false;

		color = candidate;
		end = closingBrace + 1;
		return true;
	}
}
