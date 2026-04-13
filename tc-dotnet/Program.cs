using System.Diagnostics;
using System.Security.Principal;
using Spectre.Console;
using TrueCryptReader;

// ─── SafeDrive — Encrypted Drive Management TUI ───

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    ShowHelp();
    return;
}

RunTui();
return;

// ═══════════════════════════════════════════════════════
//  TUI Main Loop
// ═══════════════════════════════════════════════════════

void RunTui()
{
    while (true)
    {
        AnsiConsole.Clear();
        WriteBanner();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]What would you like to do?[/]")
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(
                    "🔒  Create a new encrypted drive",
                    "📂  Extract data from an encrypted drive",
                    "💾  Mount as a Windows drive",
                    "❌  Exit"));

        AnsiConsole.WriteLine();

        try
        {
            if (choice.Contains("Create"))
                CreateDriveFlow();
            else if (choice.Contains("Extract"))
                ExtractDataFlow();
            else if (choice.Contains("Mount"))
                MountDriveFlow();
            else
                break;
        }
        catch (InvalidPasswordException)
        {
            ShowError("Authentication Failed", "Wrong password or not a valid TrueCrypt/SafeDrive volume.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError("Operation Failed", ex.Message);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to return to menu...[/]");
        Console.ReadKey(true);
    }

    AnsiConsole.MarkupLine("[dim]Goodbye![/]");
}

// ═══════════════════════════════════════════════════════
//  Create New Encrypted Drive
// ═══════════════════════════════════════════════════════

void CreateDriveFlow()
{
    AnsiConsole.Write(new Rule("[cyan bold]Create New Encrypted Drive[/]").RuleStyle("dim"));
    AnsiConsole.WriteLine();

    // Volume path
    var volumePath = AnsiConsole.Prompt(
        new TextPrompt<string>("  [bold]Volume file path:[/]")
            .DefaultValue("safedrive.tc")
            .Validate(path =>
                File.Exists(path)
                    ? ValidationResult.Error($"[red]File already exists: {path}[/]")
                    : ValidationResult.Success()));

    // Volume size
    var sizeChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("  [bold]Volume size:[/]")
            .HighlightStyle(new Style(Color.Cyan1))
            .AddChoices("10 MB", "50 MB", "100 MB", "256 MB", "512 MB", "1 GB", "Custom"));

    long sizeBytes;
    if (sizeChoice == "Custom")
    {
        var sizeStr = AnsiConsole.Prompt(
            new TextPrompt<string>("  [bold]Enter size[/] [dim](e.g. 200MB, 2GB)[/][bold]:[/]")
                .Validate(s => ParseSize(s) > 0
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Invalid size format. Examples: 200MB, 2GB[/]")));
        sizeBytes = ParseSize(sizeStr);
    }
    else
    {
        sizeBytes = ParseSize(sizeChoice.Replace(" ", ""));
    }

    // Password
    var password = PromptNewPassword();

    AnsiConsole.WriteLine();

    // Show configuration summary
    AnsiConsole.Write(new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Grey)
        .AddColumn(new TableColumn("[bold]Setting[/]").PadRight(2))
        .AddColumn(new TableColumn("[bold]Value[/]"))
        .AddRow("File", Markup.Escape(Path.GetFullPath(volumePath)))
        .AddRow("Size", FormatSize((ulong)sizeBytes))
        .AddRow("Encryption", "AES-256")
        .AddRow("Hash", "SHA-512")
        .AddRow("Filesystem", "NTFS"));
    AnsiConsole.WriteLine();

    // Create with progress bar
    AnsiConsole.Progress()
        .AutoClear(false)
        .HideCompleted(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .Start(ctx =>
        {
            var task = ctx.AddTask("[cyan]Initializing...[/]", maxValue: 100);

            VolumeCreator.Create(volumePath, sizeBytes, password, progress =>
            {
                task.Value = progress * 100;
                task.Description = progress switch
                {
                    <= 0.10 => "[cyan]Deriving encryption key...[/]",
                    <= 0.20 => "[cyan]Encrypting headers...[/]",
                    <= 0.30 => "[cyan]Formatting filesystem...[/]",
                    < 0.90 => "[cyan]Encrypting data area...[/]",
                    < 1.00 => "[cyan]Writing backup headers...[/]",
                    _ => "[green]Complete![/]"
                };
            });

            task.Value = 100;
            task.Description = "[green]✓ Complete[/]";
        });

    AnsiConsole.WriteLine();
    AnsiConsole.Write(
        new Panel(
            $"[green bold]Encrypted drive created successfully![/]\n\n" +
            $"  Path:       [bold]{Markup.Escape(Path.GetFullPath(volumePath))}[/]\n" +
            $"  Size:       {FormatSize((ulong)sizeBytes)}\n" +
            $"  Encryption: AES-256 / SHA-512")
        .Header("[green bold] ✅ Success [/]")
        .BorderColor(Color.Green)
        .Expand()
        .Padding(1, 1));
}

// ═══════════════════════════════════════════════════════
//  Extract Data from Encrypted Drive
// ═══════════════════════════════════════════════════════

void ExtractDataFlow()
{
    AnsiConsole.Write(new Rule("[cyan bold]Extract Data from Encrypted Drive[/]").RuleStyle("dim"));
    AnsiConsole.WriteLine();

    // Volume path
    var volumePath = AnsiConsole.Prompt(
        new TextPrompt<string>("  [bold]Volume file path:[/]")
            .Validate(path =>
                File.Exists(path)
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]File not found: {path}[/]")));

    AnsiConsole.MarkupLine($"  [dim]Volume size: {FormatSize((ulong)new FileInfo(volumePath).Length)}[/]");

    // Output directory
    var outputDir = AnsiConsole.Prompt(
        new TextPrompt<string>("  [bold]Output directory:[/]")
            .DefaultValue(Path.Combine(Directory.GetCurrentDirectory(), "decrypted")));

    // Password
    var password = PromptPassword();

    AnsiConsole.WriteLine();

    // Open volume with status spinner
    TrueCryptVolume? volume = null;
    AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(new Style(Color.Cyan1))
        .Start("[cyan]Decrypting volume header...[/]", ctx =>
        {
            volume = TrueCryptVolume.Open(volumePath, password, statusCallback: status =>
                ctx.Status($"[cyan]{Markup.Escape(status)}[/]"));
        });

    using (volume)
    {
        // Show volume info
        ShowVolumeInfo(volume!);

        // Extract with progress bar
        int fileCount = 0;
        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("[cyan]Scanning files...[/]", maxValue: 1);
                task.IsIndeterminate = true;

                fileCount = volume!.ExtractAll(outputDir, (fileName, current, total) =>
                {
                    if (task.IsIndeterminate)
                    {
                        task.IsIndeterminate = false;
                        task.MaxValue = total;
                    }
                    task.Value = current;
                    task.Description = $"[cyan]Extracting:[/] {Markup.Escape(Path.GetFileName(fileName))}";
                });

                task.Description = "[green]✓ Extraction complete[/]";
            });

        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Panel(
                $"[green bold]Extraction complete![/]\n\n" +
                $"  Files:  [bold]{fileCount}[/] file(s) extracted\n" +
                $"  Output: [bold]{Markup.Escape(Path.GetFullPath(outputDir))}[/]")
            .Header("[green bold] ✅ Success [/]")
            .BorderColor(Color.Green)
            .Expand()
            .Padding(1, 1));
    }
}

// ═══════════════════════════════════════════════════════
//  Mount as Windows Drive
// ═══════════════════════════════════════════════════════

void MountDriveFlow()
{
    AnsiConsole.Write(new Rule("[cyan bold]Mount as Windows Drive[/]").RuleStyle("dim"));
    AnsiConsole.WriteLine();

    // Check admin privileges
    if (!IsRunningAsAdmin())
    {
        AnsiConsole.Write(
            new Panel(
                "[yellow]Administrator privileges are required to mount volumes as Windows drives.[/]\n" +
                "The app will relaunch with elevated permissions.")
            .Header("[yellow bold] ⚠ Elevation Required [/]")
            .BorderColor(Color.Yellow)
            .Expand()
            .Padding(1, 1));

        AnsiConsole.WriteLine();

        if (AnsiConsole.Confirm("  Relaunch as Administrator?", defaultValue: true))
        {
            RelaunchAsAdmin();
            Environment.Exit(0);
        }
        return;
    }

    // Volume path
    var volumePath = AnsiConsole.Prompt(
        new TextPrompt<string>("  [bold]Volume file path:[/]")
            .Validate(path =>
                File.Exists(path)
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]File not found: {path}[/]")));

    // Password
    var password = PromptPassword();

    AnsiConsole.WriteLine();

    // Verify password first
    TrueCryptVolume? testVolume = null;
    AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(new Style(Color.Cyan1))
        .Start("[cyan]Verifying password...[/]", ctx =>
        {
            testVolume = TrueCryptVolume.Open(volumePath, password, statusCallback: status =>
                ctx.Status($"[cyan]{Markup.Escape(status)}[/]"));
        });

    using (testVolume)
    {
        ShowVolumeInfo(testVolume!);
    }

    // Mount
    using var mounter = new VolumeMounter(volumePath, password);
    string driveLetter = "";

    AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(new Style(Color.Cyan1))
        .Start("[cyan]Mounting volume...[/]", ctx =>
        {
            driveLetter = mounter.Mount(status =>
                ctx.Status($"[cyan]{Markup.Escape(status)}[/]"));
        });

    AnsiConsole.WriteLine();

    if (driveLetter != "unknown")
    {
        AnsiConsole.Write(
            new Panel(
                $"[green bold]Volume mounted successfully![/]\n\n" +
                $"  Drive: [bold]{driveLetter}\\[/]\n\n" +
                "[dim]You can now use this drive in Windows Explorer.\n" +
                "Press Enter below when you're done to unmount safely.[/]")
            .Header("[green bold] ✅ Mounted [/]")
            .BorderColor(Color.Green)
            .Expand()
            .Padding(1, 1));
    }
    else
    {
        AnsiConsole.Write(
            new Panel(
                "[yellow]VHD mounted but no drive letter was assigned.[/]\n" +
                "Check Disk Management to assign a drive letter.\n\n" +
                "[dim]Press Enter below when you're done to unmount safely.[/]")
            .Header("[yellow bold] ⚠ Mounted (No Drive Letter) [/]")
            .BorderColor(Color.Yellow)
            .Expand()
            .Padding(1, 1));
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("  [bold]Press Enter to unmount and save changes...[/]");
    Console.ReadLine();

    AnsiConsole.WriteLine();

    // Unmount with progress bar
    AnsiConsole.Progress()
        .AutoClear(false)
        .HideCompleted(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .Start(ctx =>
        {
            var task = ctx.AddTask("[cyan]Unmounting...[/]", maxValue: 100);

            mounter.Unmount(
                statusCallback: status => task.Description = $"[cyan]{Markup.Escape(status)}[/]",
                writeProgress: (written, total) =>
                {
                    if (total > 0)
                        task.Value = (double)written / total * 100;
                });

            task.Value = 100;
            task.Description = "[green]✓ Unmounted and saved[/]";
        });

    AnsiConsole.WriteLine();
    AnsiConsole.Write(
        new Panel(
            "[green bold]Volume safely unmounted![/]\n" +
            "All changes have been written back to the encrypted volume.")
        .Header("[green bold] ✅ Done [/]")
        .BorderColor(Color.Green)
        .Expand()
        .Padding(1, 1));
}

// ═══════════════════════════════════════════════════════
//  Shared UI Components
// ═══════════════════════════════════════════════════════

void WriteBanner()
{
    AnsiConsole.Write(
        new FigletText("SafeDrive")
            .Color(Color.Cyan1)
            .Centered());
    AnsiConsole.Write(
        new Rule("[dim]Encrypted Drive Management[/]")
            .RuleStyle("cyan")
            .Centered());
    AnsiConsole.WriteLine();
}

string PromptPassword()
{
    return AnsiConsole.Prompt(
        new TextPrompt<string>("  [bold]Password:[/]")
            .Secret()
            .Validate(p => string.IsNullOrEmpty(p)
                ? ValidationResult.Error("[red]Password cannot be empty.[/]")
                : ValidationResult.Success()));
}

string PromptNewPassword()
{
    var password = AnsiConsole.Prompt(
        new TextPrompt<string>("  [bold]Encryption password:[/]")
            .Secret()
            .Validate(p => string.IsNullOrEmpty(p)
                ? ValidationResult.Error("[red]Password cannot be empty.[/]")
                : ValidationResult.Success()));

    AnsiConsole.Prompt(
        new TextPrompt<string>("  [bold]Confirm password:[/]")
            .Secret()
            .Validate(p => p != password
                ? ValidationResult.Error("[red]Passwords do not match.[/]")
                : ValidationResult.Success()));

    return password;
}

void ShowVolumeInfo(TrueCryptVolume volume)
{
    AnsiConsole.Write(new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Cyan1)
        .Title("[cyan bold]Volume Information[/]")
        .AddColumn(new TableColumn("[bold]Property[/]").PadRight(2))
        .AddColumn(new TableColumn("[bold]Value[/]"))
        .AddRow("Encryption", volume.Header.EncryptionAlgorithm.Name)
        .AddRow("Hash", volume.Header.Prf.ToString())
        .AddRow("Volume size", FormatSize(volume.Header.VolumeSize))
        .AddRow("Header version", $"v{volume.Header.HeaderVersion}")
        .AddRow("Sector size", $"{volume.Header.SectorSize} bytes")
        .AddRow("Hidden volume", volume.Header.IsHiddenVolume ? "[yellow]Yes[/]" : "No"));
    AnsiConsole.WriteLine();
}

void ShowError(string title, string message)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(
        new Panel($"[red]{Markup.Escape(message)}[/]")
            .Header($"[red bold] ❌ {Markup.Escape(title)} [/]")
            .BorderColor(Color.Red)
            .Expand()
            .Padding(1, 1));
}

void ShowHelp()
{
    AnsiConsole.MarkupLine("[bold cyan]SafeDrive[/] — Encrypted Drive Management");
    AnsiConsole.MarkupLine("[dim]Run without arguments to start the interactive TUI.[/]");
    AnsiConsole.MarkupLine("");
    AnsiConsole.MarkupLine("  [bold]safedrive[/]          Launch interactive mode");
    AnsiConsole.MarkupLine("  [bold]safedrive --help[/]   Show this help");
}

// ═══════════════════════════════════════════════════════
//  Utilities
// ═══════════════════════════════════════════════════════

static long ParseSize(string sizeStr)
{
    sizeStr = sizeStr.Trim().ToUpperInvariant();

    double multiplier = 1;
    string numberPart = sizeStr;

    if (sizeStr.EndsWith("GB"))
    {
        multiplier = 1024.0 * 1024 * 1024;
        numberPart = sizeStr[..^2];
    }
    else if (sizeStr.EndsWith("MB"))
    {
        multiplier = 1024.0 * 1024;
        numberPart = sizeStr[..^2];
    }
    else if (sizeStr.EndsWith("KB"))
    {
        multiplier = 1024;
        numberPart = sizeStr[..^2];
    }

    if (double.TryParse(numberPart, out double number))
        return (long)(number * multiplier);

    return -1;
}

static string FormatSize(ulong bytes) => bytes switch
{
    >= 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    >= 1024UL * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    >= 1024 => $"{bytes / 1024.0:F1} KB",
    _ => $"{bytes} B"
};

static bool IsRunningAsAdmin()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void RelaunchAsAdmin()
{
    try
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Verb = "runas",
            UseShellExecute = true
        };
        Process.Start(psi);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        AnsiConsole.MarkupLine("[red]UAC elevation was cancelled or denied.[/]");
    }
}
