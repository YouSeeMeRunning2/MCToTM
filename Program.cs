using McToTm.Converters;

namespace McToTm;

class Program
{
    static void Main(string[] args)
    {
        Console.Title = "MC to TM Converter";
        PrintBanner();

        if (args.Length >= 2)
        {
            HandleCommandLine(args);
            return;
        }

        while (true)
        {
            PrintMenu();
            string? choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1":
                    RunMapConversion();
                    break;
                case "2":
                    RunSchematicConversion();
                    break;
                case "3":
                case "q":
                case "Q":
                    return;
                default:
                    WriteColor("\n  Invalid choice.\n\n", ConsoleColor.Red);
                    break;
            }
        }
    }

    static void HandleCommandLine(string[] args)
    {
        if (args[0].Equals("map", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
        {
            var converter = new MapConverter();
            converter.Convert(args[1], args[2], Console.WriteLine);
        }
        else if (args[0].Equals("schematic", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
        {
            var origin = (x: 0, y: 10, z: 0);
            if (args.Length >= 6 && int.TryParse(args[3], out int ox) &&
                int.TryParse(args[4], out int oy) && int.TryParse(args[5], out int oz))
                origin = (ox, oy, oz);
            var converter = new SchematicConverter();
            converter.Convert(args[1], args[2], origin, Console.WriteLine);
        }
        else
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  McToTm map <world_dir> <save_dir>");
            Console.WriteLine("  McToTm schematic <file> <save_dir> [x y z]");
        }
    }

    static void RunMapConversion()
    {
        Console.WriteLine();
        WriteColor("  === MAP CONVERSION ===\n\n", ConsoleColor.Cyan);
        WriteColor("  Converts a Minecraft Java world into a TotalMiner save.\n\n", ConsoleColor.Gray);

        string worldDir = BrowseForFolder("  Select Minecraft world folder", "region");
        if (worldDir == "") return;
        if (!Directory.Exists(Path.Combine(worldDir, "region")))
        {
            WriteColor("  No 'region' folder found there.\n\n", ConsoleColor.Red);
            return;
        }

        string saveDir = SelectTmSave();
        if (saveDir == "") return;
        if (!File.Exists(Path.Combine(saveDir, "header.dat")))
        {
            WriteColor("  No 'header.dat' found there.\n\n", ConsoleColor.Red);
            return;
        }

        Console.WriteLine();
        bool skipUninhabited = PromptYesNo("  Skip uninhabited chunks?", true);
        bool filterOcean = PromptYesNo("  Remove ocean water?", true);

        Console.WriteLine();
        WriteColor("  Converting...\n\n", ConsoleColor.Green);

        try
        {
            var converter = new MapConverter
            {
                SkipUninhabited = skipUninhabited,
                FilterOceanWater = filterOcean,
            };
            converter.Convert(worldDir, saveDir, msg => WriteLog(msg));
            Console.WriteLine();
            WriteColor("  Done!\n\n", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            WriteColor($"\n  ERROR: {ex.Message}\n\n", ConsoleColor.Red);
        }
    }

    static void RunSchematicConversion()
    {
        Console.WriteLine();
        WriteColor("  === SCHEMATIC INJECTION ===\n\n", ConsoleColor.Cyan);
        WriteColor("  Injects a schematic into a TotalMiner save.\n", ConsoleColor.Gray);
        WriteColor("  Formats: .schem  .litematic  .schematic\n\n", ConsoleColor.Gray);

        string schemPath = BrowseForFile("  Select schematic file", new[] { ".schem", ".litematic", ".schematic" });
        if (schemPath == "") return;

        string saveDir = SelectTmSave();
        if (saveDir == "") return;
        if (!File.Exists(Path.Combine(saveDir, "header.dat")))
        {
            WriteColor("  No 'header.dat' found there.\n\n", ConsoleColor.Red);
            return;
        }

        Console.Write("\n  Origin X [0]: ");
        int ox = ParseIntOr(Console.ReadLine(), 0);
        Console.Write("  Origin Y [10]: ");
        int oy = ParseIntOr(Console.ReadLine(), 10);
        Console.Write("  Origin Z [0]: ");
        int oz = ParseIntOr(Console.ReadLine(), 0);

        Console.WriteLine();
        WriteColor("  Injecting...\n\n", ConsoleColor.Green);

        try
        {
            var converter = new SchematicConverter();
            converter.Convert(schemPath, saveDir, (ox, oy, oz), msg => WriteLog(msg));
            Console.WriteLine();
            WriteColor("  Done!\n\n", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            WriteColor($"\n  ERROR: {ex.Message}\n\n", ConsoleColor.Red);
        }
    }

    static string BrowseForFolder(string prompt, string? lookFor = null)
    {
        Console.WriteLine(prompt);
        Console.Write("  > ");
        string? input = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(input)) return "";
        return input;
    }

    static string SelectTmSave()
    {
        Console.WriteLine();
        WriteColor("  Select TotalMiner save\n\n", ConsoleColor.Cyan);

        var mapsDir = FindTmMapsFolder();
        if (mapsDir == null || !Directory.Exists(mapsDir))
        {
            WriteColor("  Could not find TotalMiner Maps folder.\n", ConsoleColor.Yellow);
            Console.WriteLine("  Enter save folder path manually:");
            Console.Write("  > ");
            string? manual = Console.ReadLine()?.Trim().Trim('"');
            return string.IsNullOrEmpty(manual) ? "" : manual;
        }

        var saves = new List<(string path, string name)>();
        foreach (var dir in Directory.GetDirectories(mapsDir).OrderBy(d => d))
        {
            string headerPath = Path.Combine(dir, "header.dat");
            if (!File.Exists(headerPath)) continue;
            try
            {
                var header = Core.TmRegion.ParseHeaderDat(headerPath);
                saves.Add((dir, header.MapName));
            }
            catch
            {
                saves.Add((dir, Path.GetFileName(dir)));
            }
        }

        if (saves.Count == 0)
        {
            WriteColor("  No saves found.\n", ConsoleColor.Yellow);
            Console.WriteLine("  Enter save folder path manually:");
            Console.Write("  > ");
            string? manual = Console.ReadLine()?.Trim().Trim('"');
            return string.IsNullOrEmpty(manual) ? "" : manual;
        }

        for (int i = 0; i < saves.Count; i++)
        {
            WriteColor($"  {i + 1,3}", ConsoleColor.White);
            WriteColor($"  {saves[i].name}", ConsoleColor.Gray);
            WriteColor($"  ({Path.GetFileName(saves[i].path)})\n", ConsoleColor.DarkGray);
        }

        Console.WriteLine();
        Console.Write("  > ");
        string? input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return "";

        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= saves.Count)
            return saves[idx - 1].path;

        input = input.Trim('"');
        if (Directory.Exists(input)) return input;

        WriteColor("  Invalid selection.\n\n", ConsoleColor.Red);
        return "";
    }

    static string? FindTmMapsFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(home, "OneDrive", "Documents", "My Games", "TotalMiner", "Maps"),
            Path.Combine(home, "Documents", "My Games", "TotalMiner", "Maps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "TotalMiner", "Maps"),
        };
        foreach (var path in candidates)
            if (Directory.Exists(path)) return path;
        return null;
    }

    static string BrowseForFile(string prompt, string[]? extensions = null)
    {
        Console.WriteLine(prompt);
        Console.Write("  > ");
        string? input = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(input)) return "";
        if (!File.Exists(input))
        {
            WriteColor("  File not found.\n\n", ConsoleColor.Red);
            return "";
        }
        return input;
    }

    static void PrintBanner()
    {
        Console.WriteLine();
        WriteColor("  MC to TM Converter\n", ConsoleColor.Cyan);
        WriteColor("  Minecraft -> TotalMiner\n\n", ConsoleColor.DarkGray);
    }

    static void PrintMenu()
    {
        WriteColor("  1", ConsoleColor.White);
        WriteColor("  Convert Minecraft World\n", ConsoleColor.Gray);
        WriteColor("  2", ConsoleColor.White);
        WriteColor("  Inject Schematic\n", ConsoleColor.Gray);
        WriteColor("  3", ConsoleColor.White);
        WriteColor("  Exit\n\n", ConsoleColor.Gray);
        Console.Write("  > ");
    }

    static bool PromptYesNo(string label, bool defaultVal)
    {
        string hint = defaultVal ? "Y/n" : "y/N";
        Console.Write($"{label} [{hint}]: ");
        string? input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(input)) return defaultVal;
        return input.StartsWith("y");
    }

    static int ParseIntOr(string? input, int defaultVal)
    {
        if (string.IsNullOrWhiteSpace(input)) return defaultVal;
        return int.TryParse(input.Trim(), out int val) ? val : defaultVal;
    }

    static void WriteColor(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }

    static void WriteLog(string message)
    {
        WriteColor($"  {message}\n", ConsoleColor.Gray);
    }
}
