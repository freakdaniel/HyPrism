using System;
using System.Collections.Generic;

namespace HyPrism.Services.Core;

public static class Logger
{
    private static readonly object _lock = new();
    private static readonly Queue<string> _logBuffer = new();
    private const int MaxLogEntries = 100;
    
    public static void Info(string category, string message)
    {
        WriteLog("INFO", category, message, ConsoleColor.White);
    }
    
    public static void Success(string category, string message)
    {
        WriteLog("OK", category, message, ConsoleColor.Green);
    }
    
    public static void Warning(string category, string message)
    {
        WriteLog("WARN", category, message, ConsoleColor.Yellow);
    }
    
    public static void Error(string category, string message)
    {
        WriteLog("ERR", category, message, ConsoleColor.Red);
    }
    
    public static void Debug(string category, string message)
    {
#if DEBUG
        WriteLog("DBG", category, message, ConsoleColor.Gray);
#endif
    }
    
    public static List<string> GetRecentLogs(int count = 10)
    {
        lock (_lock)
        {
            var entries = _logBuffer.ToArray();
            var start = Math.Max(0, entries.Length - count);
            var result = new List<string>();
            for (int i = start; i < entries.Length; i++)
            {
                result.Add(entries[i]);
            }
            return result;
        }
    }
    
    public static void Progress(string category, int percent, string message)
    {
        lock (_lock)
        {
            Console.Write($"\r[{category}] {message.PadRight(40)} [{ProgressBar(percent, 20)}] {percent,3}%");
            if (percent >= 100)
            {
                Console.WriteLine();
            }
        }
    }
    
    private static void WriteLog(string level, string category, string message, ConsoleColor color)
    {
        lock (_lock)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var icon = level switch
            {
                "OK" => "✔",
                "WARN" => "⚠",
                "ERR" => "✖",
                _ => "•"
            };

            var logEntry = $"{timestamp} | {level} | {category} | {message}";
            
            // Add to buffer
            _logBuffer.Enqueue(logEntry);
            while (_logBuffer.Count > MaxLogEntries)
            {
                _logBuffer.Dequeue();
            }
            
            var originalColor = Console.ForegroundColor;
            
            Console.Write($"{timestamp}  ");
            Console.ForegroundColor = color;
            Console.Write($"{icon} {level}");
            Console.ForegroundColor = originalColor;
            Console.WriteLine($"  {category}: {message}");
        }
    }
    
    private static string ProgressBar(int percent, int width)
    {
        int filled = (int)((percent / 100.0) * width);
        int empty = width - filled;
        return new string('=', filled) + new string('-', empty);
    }
}
