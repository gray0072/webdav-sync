using System.CommandLine;
using System.Text;
using WebDavSync.Cli;

Console.OutputEncoding = Encoding.UTF8;

var dataDir = new Option<string?>("--data-dir")
{
    Description = "App data folder (default: <app dir>\\data, or WDS_DATA_DIR).",
    Recursive = true,
};

var root = new RootCommand("WebDavSync — two-way folder sync with a WebDAV cloud (CLI prototype).");
root.Options.Add(dataDir);
root.Subcommands.Add(Commands.Probe(dataDir));
root.Subcommands.Add(Commands.Pair(dataDir));
root.Subcommands.Add(Commands.Import(dataDir));
root.Subcommands.Add(Commands.Scan(dataDir));
root.Subcommands.Add(Commands.Plan(dataDir));
root.Subcommands.Add(Commands.Sync(dataDir));
root.Subcommands.Add(Commands.Held(dataDir));
root.Subcommands.Add(Commands.Rule(dataDir));
root.Subcommands.Add(Commands.Secret(dataDir));
root.Subcommands.Add(Commands.Folders(dataDir));
root.Subcommands.Add(Commands.Status(dataDir));

return await root.Parse(args).InvokeAsync();
