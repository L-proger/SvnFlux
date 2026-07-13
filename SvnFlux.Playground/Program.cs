using System.CommandLine;
using SvnFlux.Playground.Scenarios;

var rootCommand = new RootCommand("Interactive scenarios for exercising SvnFlux components.");
rootCommand.Subcommands.Add(RaSvnScenario.CreateCommand());
rootCommand.Subcommands.Add(SubversionNativeScenario.CreateCommand());
rootCommand.Subcommands.Add(SubversionNativeScenario.CreateCheckoutCommand());

return rootCommand.Parse(args).Invoke();
