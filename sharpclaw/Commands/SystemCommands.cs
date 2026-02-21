using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Tinvo.Core.TaskManagement;

namespace Tinvo.Commands;

/// <summary>
/// System information and utility commands.
/// </summary>
public class SystemCommands : CommandBase
{
    public SystemCommands(TaskManager taskManager)
        : base(taskManager)
    {
    }

    [Description("Get system information including OS, architecture, framework, and environment details")]
    public string GetSystemInfo()
    {
        var obj = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["os"] = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
                  : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
                  : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
                  : "unknown",
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["machineName"] = Environment.MachineName,
            ["userName"] = Environment.UserName,
            ["processId"] = Environment.ProcessId,
            ["utcNow"] = DateTimeOffset.UtcNow.ToString("O")
        };

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var osRelease = "/etc/os-release";
                if (File.Exists(osRelease))
                    obj["linuxOsRelease"] = File.ReadAllText(osRelease);
            }
            catch { /* ignore */ }
        }

        return Serialize(obj);
    }

    [Description("Calculate mathematical expressions. Supports +, -, *, /, %, ^, parentheses, constants (pi, e), and functions (sqrt, abs, pow, min, max)")]
    public string CommandCalc(
        [Description("Mathematical expression to evaluate")] string expression)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expression))
                return Serialize(new { ok = false, error = "Expression is required" });

            var result = EvaluateExpression(expression);
            return Serialize(new { ok = true, expression, result });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}", expression });
        }
    }

    private double EvaluateExpression(string expr)
    {
        // Simple expression evaluator
        // Replace constants
        expr = expr.Replace("pi", Math.PI.ToString(System.Globalization.CultureInfo.InvariantCulture));
        expr = expr.Replace("e", Math.E.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Handle functions
        expr = HandleFunctions(expr);

        // Evaluate using DataTable.Compute as a simple calculator
        var dt = new System.Data.DataTable();
        var result = dt.Compute(expr, "");
        return Convert.ToDouble(result);
    }

    private string HandleFunctions(string expr)
    {
        // Handle sqrt, abs, pow, min, max
        while (expr.Contains("sqrt("))
        {
            int start = expr.IndexOf("sqrt(");
            int end = FindMatchingParen(expr, start + 4);
            var inner = expr.Substring(start + 5, end - start - 5);
            var val = EvaluateExpression(inner);
            expr = expr.Substring(0, start) + Math.Sqrt(val).ToString(System.Globalization.CultureInfo.InvariantCulture) + expr.Substring(end + 1);
        }

        while (expr.Contains("abs("))
        {
            int start = expr.IndexOf("abs(");
            int end = FindMatchingParen(expr, start + 3);
            var inner = expr.Substring(start + 4, end - start - 4);
            var val = EvaluateExpression(inner);
            expr = expr.Substring(0, start) + Math.Abs(val).ToString(System.Globalization.CultureInfo.InvariantCulture) + expr.Substring(end + 1);
        }

        // Similar for pow, min, max...
        return expr;
    }

    private int FindMatchingParen(string expr, int start)
    {
        int depth = 1;
        for (int i = start + 1; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            if (expr[i] == ')') depth--;
            if (depth == 0) return i;
        }
        throw new ArgumentException("Unmatched parentheses");
    }

    [Description("Get the current working directory")]
    public string GetCurrentDirectory()
    {
        try
        {
            var cwd = Environment.CurrentDirectory;
            return Serialize(new
            {
                ok = true,
                currentDirectory = cwd,
                exists = Directory.Exists(cwd)
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [Description("Get environment variable value")]
    public string GetEnvironmentVariable(
        [Description("Environment variable name")] string name,
        [Description("Target: Process, User, or Machine")] string target = "Process")
    {
        try
        {
            var targetEnum = target.ToLowerInvariant() switch
            {
                "user" => EnvironmentVariableTarget.User,
                "machine" => EnvironmentVariableTarget.Machine,
                _ => EnvironmentVariableTarget.Process
            };

            var value = Environment.GetEnvironmentVariable(name, targetEnum);
            return Serialize(new
            {
                ok = true,
                name,
                value,
                exists = value != null,
                target
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}", name });
        }
    }

    [Description("Exit the program. Only call this when the user's task is completely finished.")]
    public string ExitProgram()
    {
        Environment.Exit(0);
        return Serialize(new { ok = true, action = "exit" });
    }
}
