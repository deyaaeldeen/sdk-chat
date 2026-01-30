// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Spectre.Console;

namespace Sdk.Tools.Chat.Helpers;

/// <summary>
/// Professional CLI UX using Spectre.Console.
/// Provides rich spinners, progress bars, tables, and styled output.
/// Use this for enhanced UX when terminal supports it.
/// </summary>
public static class SpectreConsoleUx
{
    /// <summary>
    /// Runs an async operation with a professional spinner.
    /// </summary>
    public static async Task<T> SpinnerAsync<T>(string message, Func<Task<T>> action)
    {
        T result = default!;
        
        await AnsiConsole.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async ctx =>
            {
                result = await action();
            });
        
        AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(message)}");
        return result;
    }
    
    /// <summary>
    /// Runs an async operation with a spinner (no return value).
    /// </summary>
    public static async Task SpinnerAsync(string message, Func<Task> action)
    {
        await SpinnerAsync(message, async () => { await action(); return 0; });
    }
    
    /// <summary>
    /// Creates a live progress context for streaming operations.
    /// </summary>
    public static async Task<T> ProgressAsync<T>(string title, Func<ProgressContext, Task<T>> action)
    {
        T result = default!;
        
        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                result = await action(ctx);
            });
        
        return result;
    }
    
    /// <summary>
    /// Displays a table of data.
    /// </summary>
    public static void Table(string title, params (string Header, string[] Values)[] columns)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title(title)
            .Expand();
        
        foreach (var (header, _) in columns)
        {
            table.AddColumn(new TableColumn(header).Centered());
        }
        
        var rowCount = columns.Length > 0 ? columns[0].Values.Length : 0;
        for (var i = 0; i < rowCount; i++)
        {
            var row = columns.Select(c => i < c.Values.Length ? c.Values[i] : "").ToArray();
            table.AddRow(row);
        }
        
        AnsiConsole.Write(table);
    }
    
    /// <summary>
    /// Displays a tree structure.
    /// </summary>
    public static void Tree(string title, params string[] items)
    {
        var tree = new Tree(title);
        foreach (var item in items)
        {
            tree.AddNode(item);
        }
        AnsiConsole.Write(tree);
    }
    
    /// <summary>
    /// Writes a success message.
    /// </summary>
    public static void Success(string message) => 
        AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(message)}");
    
    /// <summary>
    /// Writes an error message.
    /// </summary>
    public static void Error(string message) => 
        AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(message)}");
    
    /// <summary>
    /// Writes an info message (dimmed).
    /// </summary>
    public static void Info(string message) => 
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(message)}[/]");
    
    /// <summary>
    /// Writes a warning message.
    /// </summary>
    public static void Warning(string message) => 
        AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(message)}");
    
    /// <summary>
    /// Writes a header.
    /// </summary>
    public static void Header(string message) => 
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(message)}[/]").LeftJustified());
    
    /// <summary>
    /// Creates a panel with content.
    /// </summary>
    public static void Panel(string title, string content)
    {
        var panel = new Panel(Markup.Escape(content))
            .Header(title)
            .Border(BoxBorder.Rounded)
            .Padding(1, 0);
        AnsiConsole.Write(panel);
    }
    
    /// <summary>
    /// Asks for user confirmation.
    /// </summary>
    public static bool Confirm(string message) => 
        AnsiConsole.Confirm(message);
    
    /// <summary>
    /// Creates a selection prompt.
    /// </summary>
    public static T Select<T>(string title, IEnumerable<T> choices) where T : notnull =>
        AnsiConsole.Prompt(
            new SelectionPrompt<T>()
                .Title(title)
                .AddChoices(choices));
    
    /// <summary>
    /// Live display for streaming content.
    /// </summary>
    public static async Task LiveAsync(string title, Func<LiveDisplayContext, Task> action)
    {
        await AnsiConsole.Live(new Panel($"[dim]Starting...[/]").Header(title))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(action);
    }
    
    /// <summary>
    /// Displays generated samples in a tree format.
    /// </summary>
    public static void DisplaySamples(string outputPath, IEnumerable<string> samplePaths)
    {
        var tree = new Tree($"[bold]Generated Samples[/] → [blue]{Markup.Escape(outputPath)}[/]");
        
        foreach (var path in samplePaths)
        {
            var relativePath = Path.GetRelativePath(outputPath, path);
            tree.AddNode($"[green]✓[/] {Markup.Escape(relativePath)}");
        }
        
        AnsiConsole.Write(tree);
    }
    
    /// <summary>
    /// Displays a summary panel.
    /// </summary>
    public static void Summary(string title, params (string Label, string Value)[] items)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(2))
            .AddColumn();
        
        foreach (var (label, value) in items)
        {
            grid.AddRow($"[dim]{Markup.Escape(label)}:[/]", Markup.Escape(value));
        }
        
        var panel = new Panel(grid)
            .Header($"[bold]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded)
            .Padding(1, 0);
        
        AnsiConsole.Write(panel);
    }
}
