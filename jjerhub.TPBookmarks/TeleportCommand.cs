using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System;
using UnityEngine;
using Character;
using System.Threading.Tasks;
using System.Threading;
using System.Transactions;
using Railloader;
using Serilog;
using Serilog.Events;
using Serilog.Configuration;
using Serilog.Core;
using System.Diagnostics;
using Helpers;
using Newtonsoft.Json.Serialization;

namespace jjerhub.TPBookmarks
{
    public static class LoggerHelper
    {
        public static void Info(this Serilog.ILogger logger, string message)
        {
            logger.Information(message);
        }

        public static void Error(this Serilog.ILogger logger, Exception ex)
        {
            logger.Error(ex, ex?.Message);
        }

        public static LoggerConfiguration MySerilogSink(this LoggerSinkConfiguration sinkConfig, IFormatProvider formatProvider = null) => sinkConfig.Sink(new MySerilogSink(null));
        public static LoggerConfiguration MyLogEnricher(this LoggerEnrichmentConfiguration config, IFormatProvider formatProvider = null) => config.With(new MyLogEnricher());
    }

    public class MySerilogSink : ILogEventSink
    {
        private readonly IFormatProvider formatProvider;

        public MySerilogSink(IFormatProvider formatProvider)
        {
            this.formatProvider = formatProvider;
        }

        public void Emit(LogEvent logEvent)
        {
            var asmLocation = Assembly.GetExecutingAssembly().Location;
            var logFilePath = asmLocation + "TPBookmarks-log.txt";
            var message = logEvent.RenderMessage(formatProvider);
            var fi = new FileInfo(logFilePath);

            if (fi.Exists && fi.LastWriteTimeUtc.Day < DateTime.UtcNow.Day)
            {
                fi.CopyTo(logFilePath.Replace(".txt", $"-{fi.LastWriteTimeUtc.ToString("dd_mm_yyyy")}.txt"));
            }

            File.AppendAllText(logFilePath, DateTimeOffset.Now.ToString() + " " + message);

            //do cleanup
            Task.Run(() =>
            {
                var di = new DirectoryInfo(asmLocation);
                var files = di.GetFiles();

                if (files.Length > 0)
                {
                    var skipFiles = 3;
                    var skipped = 0;
                    var enumerator = files.OrderByDescending(f => f.LastWriteTimeUtc).GetEnumerator();

                    while (skipped++ < skipFiles && enumerator.MoveNext()) ;
                    while (enumerator.MoveNext()) try { enumerator.Current.Delete(); } finally { }
                }
            });
        }
    }

    public class MyLogEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) => 
            typeof(MyLogEnricher)
            .GetMethod("Enrich")
            .MakeGenericMethod(new StackTrace().GetFrame(1).GetMethod().ReflectedType)
            .Invoke(this, new object[] { logEvent, propertyFactory });

        public void Enrich<T>(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) where T : class
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Context", nameof(T)));
        }
    }

    public class CommandList
    {
        public string[] Commands;
        public string Description;
    }

    public class TeleportCommand : PluginBase
    {
        static Harmony harmony = new Harmony("com.jjerhub.TPBookmarks");
        static MethodBase original = typeof(UI.Console.Commands.TeleportCommand).GetMethod("Execute");
        static readonly string asmPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        static readonly string bookmarkPath = Path.Combine(asmPath, "bookmarks.json");
        static readonly bool showDescription = true; //TODO: make this a setting
        static readonly object lockObj = new object();
        static Task writeWait = null;
        static CancellationTokenSource writeCancel = null;
        static Serilog.Core.LoggingLevelSwitch logLevel = new Serilog.Core.LoggingLevelSwitch();
        private static Serilog.ILogger Logger = null;
        private readonly IModDefinition self;
        private IUIHelper uIHelper;
        internal IModdingContext ModdingContext { get; private set; }

        public TeleportCommand(IModDefinition self, IModdingContext moddingContext, IUIHelper uIHelper)
        {
            this.self = self;
            this.uIHelper = uIHelper;
            ModdingContext = moddingContext;

            Init();
        }

        public void Init()
        {
            var myPostfixMethod = typeof(TeleportCommand).GetMethod("EndTeleport");
            var myPrefixMethod = typeof(TeleportCommand).GetMethod("BeginTeleport");
            //Log.Logger = new LoggerConfiguration()
            //.MinimumLevel.ControlledBy(logLevel)
            //.Enrich.MyLogEnricher()
            //.WriteTo.MySerilogSink()
            //.CreateLogger();

            if (Logger == null) Logger = Log.ForContext<TeleportCommand>();

#if DEBUG
            logLevel.MinimumLevel = LogEventLevel.Debug;
#endif

            Logger.Info($"Trying harmony patch...");
            harmony.Patch(original, prefix: new HarmonyMethod(myPrefixMethod), postfix: new HarmonyMethod(myPostfixMethod));
            Logger.Info("Harmony patch finished...");
        }

        public static void BeginTeleport(ref PatchState __state, string[] comps)
        {
            __state = new PatchState() { Args = comps };
        }

        public static void EndTeleport(ref PatchState __state, ref string __result)
        {
            Logger.Debug($"Result before patching: \"{__result}\"");

            if (__result == "nothing") return;

            if (!__result.StartsWith("Jump to") && !__result.StartsWith("Follow"))
            {
                try
                {
                    ushort depth;
                    FileInfo fi = new FileInfo(bookmarkPath);

                    if (fi.Exists && fi.LastWriteTimeUtc.Subtract(__state.LastAdditionModified).TotalSeconds > 1)
                    {
                        var rawAdditions = JsonConvert.DeserializeObject<IEnumerable<KeyValuePair<uint, TPAddition>>>(File.ReadAllText(bookmarkPath));
                        __state.Additions = new TPAdditions(rawAdditions);
                        __state.LastAdditionModified = DateTime.UtcNow;
                    }

                    var (arg, newOutput, wasCommand) = ValidateAndCommand(ref __state, bookmarkPath);

                    if (!__result.StartsWith("Jump") && !__result.StartsWith("Follow"))
                    {
                        if (String.IsNullOrWhiteSpace(newOutput) && !wasCommand) (newOutput, depth) = HandleTP(ref __state, ref __result, arg, depth: 0);

                        __result = newOutput;
                    }
                }
                finally
                {
                    __state = default;
                }
            }
        }

        private static (string output, ushort depth) HandleTP(ref PatchState __state, ref string formerOutput, string arg, ushort depth)
        {
            TPAddition addition = __state.Additions?.FirstOrDefault(a => a.Keyword.Trim().ToLowerInvariant() == arg);

            Logger.Info($"Handling TP to: \"{arg}\"");
            if (addition != null && (!String.IsNullOrWhiteSpace(addition?.Coords) || !String.IsNullOrWhiteSpace(addition?.AliasFor)))
            {
                Vector3? location = null;
                Quaternion? rotation = null;
                TPAddition thisAddition = addition ?? default;

                Logger.Info($"Checking for alias redirect for \"{arg}\"");
                if (String.IsNullOrWhiteSpace(thisAddition.AliasFor)) (location, rotation) = ParseCoords(addition.Coords);
                else if (depth == 0)
                {
                    Logger.Info($"Alias found: \"{thisAddition.AliasFor}\", attempting redirect");
                    if (__state.Additions.Any(a => a.Keyword.Trim().ToLowerInvariant() == thisAddition.AliasFor.Trim().ToLowerInvariant()))
                    {
                        return HandleTP(ref __state, ref formerOutput, thisAddition.AliasFor, ++depth);
                    }
                    else
                    {
                        var spawnPoint = SpawnPoint.All.FirstOrDefault(p => String.Compare(p.name, thisAddition.AliasFor.Trim().ToLowerInvariant(), ignoreCase: true) == 0);

                        if (spawnPoint != null) (location, rotation) = spawnPoint.GamePositionRotation;
                    }
                }
                else Logger.Info($"Unable to process alias: \"{thisAddition.AliasFor}\". Too much recursion.");

                if (location != null)
                {
                    //var camera = CameraSelector.shared.strategyCamera;
                    var camera = CameraSelector.shared;

                    if (rotation == null) rotation = Quaternion.identity;

                    camera.JumpToPoint(location.Value, rotation.Value);
                    //CameraSelector.shared.SetCamera(CameraSelector.CameraIdentifier.Strategy, CameraSelector.shared.GetComponent<ICameraSelectable>());
                    return ($"Jump to {thisAddition.Keyword}" + ((showDescription && thisAddition.Description != null) ? $" - \"{thisAddition.Description}\"" : ""), depth);
                }
                else Logger.Info($"Unable to parse location for TP target: \"{arg}\"");
            }
            else Logger.Info($"Unable to TP to: \"{arg}\"");

            return (formerOutput, depth);
        }

        private static (string arg, string output, bool wasCommand) ValidateAndCommand(ref PatchState __state, string bookmarkPath)
        {
            var commands = new CommandList[]{
                new CommandList(){ Commands = new string[]{ "add" }, Description = "Use: /tp <command> <target>[ \"<description>]\"" }, 
                new CommandList(){ Commands = new string[]{ "update", "upd" }, Description = "Use: /tp <command> <target>[ \"<description>\"]" },
                new CommandList(){ Commands = new string[]{ "remove", "rm" }, Description = "Use: /tp <command> <target>" }, 
                new CommandList(){ Commands = new string[]{ "?" } }, 
                new CommandList(){ Commands = new string[]{ "alias", "aka" }, Description = "Use: /tp <command> <target> <alias>[ \"<description>\"]" }, 
                new CommandList(){ Commands = new string[]{ "rename", "rn" }, Description = "Use: /tp <command> <oldName> <newName>" },
                new CommandList(){ Commands = new string[]{ "info", "list", "ls" }, Description = "Use: /tp list[ <target>]" },
            };
            string output = String.Empty;
            string command = null;
            string arg = null;
            var args = __state.Args;
            bool wasCommand = false;

            if (args.Length > 1 && args.Length <= 5 && commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => args[1].ToLowerInvariant() == cmd)))
            {
                TPAddition newItem = null;

                if (args.Length > 2 && commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => args[2].Trim('\"').ToLowerInvariant() == cmd))) output = $"\"{args[2].Trim('\"')}\" is an invalid name for a teleport target because it is a reserved word.";

                wasCommand = true;
                command = args[1].ToLowerInvariant();
                if (args.Length > 2)
                {
                    arg = args[2].Trim('\"');
                    newItem = __state.Additions?.FirstOrDefault(a => a.Keyword.ToLowerInvariant() == arg.ToLowerInvariant());

                    Logger.Debug($"Check create: {arg}, keyword: {newItem?.Keyword}");
                    if (String.IsNullOrWhiteSpace(newItem?.Keyword)) newItem = new TPAddition() { Keyword = arg };
                }

                if (newItem != null)
                {
                    if (args.Length >= 4 && (args[3]?.Trim('\"').Length ?? 1) != 0)
                    {
                        if (commands[4].Commands.Contains(command)) //alias
                        {
                            if (commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => args[3]?.Trim('\"') == cmd))) output = $"\"{args[3]}\" is an invalid name for an alias target because it is a reserved word.";
                            else
                            {
                                newItem.AliasFor = args[3]?.Trim('\"');

                                if (args.Length > 4) newItem.Description = args[4]?.Trim('\"');
                            }
                        }
                        else if (commands[5].Commands.Contains(command)) //rename
                        {
                            if (commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => args[3].Trim('\"') == cmd))) output = $"\"{args[3]}\" is an invalid name for a teleport target because it is a reserved word.";
                            else newItem.Keyword = args[3].Trim('\"');
                        }
                        else if (commands[0].Commands.Contains(command) || commands[1].Commands.Contains(command)) newItem.Description = args[3]?.Trim('\"'); //add/update
                    }
                }

                if (commands[0].Commands.Contains(command)) //add
                {
                    if (((IEnumerable<TPAddition>)__state.Additions)?.Any(a => a.Keyword.Trim().ToLowerInvariant() == arg.ToLowerInvariant()) ?? false) output = $"\"{arg}\" is already listed as a keyword.  You must specify a keyword that doesn't already exist, or use the \"update\" command.\n";
                    else if (arg == null) output = "You must specify a TP target when using the add command";
                    else
                    {
                        Logger.Debug("Attempting to add new TP location");

                        using (var scope = new TransactionScope())
                        {
                            newItem.Coords = GetSerializedCoords(command[command.Length - 1] == 'r');

                            if (__state.Additions == null) __state.Additions = new TPAdditions();

                            __state.Additions[newItem.Keyword.ToLowerInvariant()] = newItem;
                            Logger.Debug($"additions contains {__state.Additions.Count} items before writing to disk");
                            Logger.Debug($"additions first keyword: {__state.Additions.FirstOrDefault(i => true).Keyword}");
                            WriteToFile(ref __state);
                            output = $"Successfully added new TP target: \"{arg}\"";

                            scope.Complete();
                        }
                    }
                }
                else if (commands[1].Commands.Contains(command)) //update
                {
                    Logger.Debug("Attempting to update TP location");
                    using (var scope = new TransactionScope())
                    {
                        if (((IEnumerable<TPAddition>)__state.Additions)?.Any(a => a.Keyword.ToLowerInvariant() == arg.ToLowerInvariant()) ?? false)
                        {
                            if (newItem.Description == null) newItem.Coords = GetSerializedCoords(command[command.Length - 1] == 'r');

                            if (newItem.Description == "null") newItem.Description = null;
                            __state.Additions[arg.ToLowerInvariant()] = newItem;
                            WriteToFile(ref __state);
                            output = $"Successfully updated TP target: \"{newItem.Keyword}\"";
                        }
                        else if (arg == null) output = "You must specify a TP target when using the update command";
                        else output = $"Unable to find \"{arg}\" listed as a keyword. Perhaps you should \"add\" it?";

                        scope.Complete();
                    }
                }
                else if (commands[2].Commands.Contains(command)) //remove
                {
                    using (var scope = new TransactionScope())
                    {
                        if (((IEnumerable<TPAddition>)__state.Additions)?.Any(a => a.Keyword.ToLowerInvariant() == arg.ToLowerInvariant()) ?? false)
                        {
                            __state.Additions.Remove(arg.ToLowerInvariant());
                            WriteToFile(ref __state);
                            output = $"Successfully removed TP target: \"{arg}\"";
                        }
                        else if (arg == null) output = "You must specify a TP target when using the remove command";
                        else output = $"Unable to find \"{arg}\" listed as a keyword. Perhaps you should \"add\" it?";

                        scope.Complete();
                    }
                }
                else if (commands[4].Commands.Contains(command)) //alias
                {
                    using (var scope = new TransactionScope())
                    {
                        if (arg == null) output = "You must specify a TP target when using the alias command";
                        else if (String.IsNullOrWhiteSpace(newItem.AliasFor)) output = "You must specify an alias name when using the alias command";
                        else
                        {
                            Logger.Debug("Attempting write alias for: " + newItem.Keyword);
                            if (newItem.Description == "null") newItem.Description = null;
                            __state.Additions[arg.ToLowerInvariant()] = newItem;
                            WriteToFile(ref __state);
                            output = $"Successfully set TP target: \"{newItem.Keyword}\" as an alias for: \"{newItem.AliasFor}\"";
                        }

                        scope.Complete();
                    }
                }
                else if (commands[5].Commands.Contains(command)) //rename
                {
                    using (var scope = new TransactionScope())
                    {
                        if (newItem.Keyword != null)
                        {
                            var oldItem = __state.Additions[arg.ToLowerInvariant()];

                            if (oldItem?.Keyword != null)
                            {
                                if (oldItem?.AliasFor != newItem.Keyword)
                                {
                                    newItem.Description = oldItem?.Description;
                                    newItem.Coords = oldItem?.Coords;
                                    newItem.AliasFor = oldItem?.AliasFor;
                                    __state.Additions.Remove(arg.ToLowerInvariant());
                                    __state.Additions[newItem.Keyword.ToLowerInvariant()] = newItem;
                                    WriteToFile(ref __state);
                                    output = $"Successfully renamed TP target: \"{oldItem.Keyword}\" to: \"{newItem.Keyword}\"";
                                }
                                else output = $"Unable to rename \"{arg}\" to \"{newItem.Keyword}\" because this target would become an alias of itself.";
                            }
                            else output = $"Unable to rename \"{arg}\" to \"{newItem.Keyword}\". Unable to find a teleport target for \"{arg}\".";
                        }
                        else
                        {
                            output = "You must specify a TP target to rename";
                        }

                        scope.Complete();
                    }
                }
                else if (commands[6].Commands.Contains(command)) //info/list
                { 
                    if (arg == null)
                    {
                        var rendered = ((IEnumerable<TPAddition>)__state.Additions).Select(a => a.Keyword.ToLowerInvariant());
#if DEBUG
                    var mappedValues = new string[] { "test me" };
#else
                        var mappedValues = SpawnPoint.All.OrderBy(sp => sp.name.ToLowerInvariant()).Select(sp => sp.name.ToLowerInvariant());
#endif
                        rendered = rendered.Union(mappedValues);
                        output = $"Mapped TP targets:\n{WrappedLine(String.Join(", ", rendered))}";
                    }
                    else
                    {
                        var found = ((IEnumerable<TPAddition>)__state.Additions).FirstOrDefault(a => a.Keyword.ToLowerInvariant() == arg.ToLowerInvariant());

                        if (found != null)
                        {
                            output = $"Found: {found.Keyword}{(found.Description != null ? $" - {found.Description}" : "")}";
                        }
                        else
                        {
                            output = $"TP bookmark: {arg} doesn't exist";
                        }
                    }
                }
            }
            else if (args.Length > 1 && args.Length < 3)
            {
                arg = args[1].ToLowerInvariant();
            }
#if DEBUG
            Logger.Info($"checking state before final error check. arg: \"{arg}\", cmd: \"{command}\", output: \"{output}\"");
#endif
            if (args.Length > 0
                &&
                (
                    (String.IsNullOrWhiteSpace(arg) && !commands[6].Commands.Contains(command)) //see if its a list/info command
                    || arg == "?"
                    || (!String.IsNullOrWhiteSpace(command) && commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => command == cmd)) && String.IsNullOrWhiteSpace(arg) && !commands[6].Commands.Contains(command))
                    || (
                            !String.IsNullOrWhiteSpace(output)
                            && !output.StartsWith("Success")
                            && !output.StartsWith("Mapped")
                            && !output.StartsWith("Found")
                       )
                )
            )
            {
#if DEBUG
                var mappedValues = "test me";
#else
                var mappedValues = SpawnPoint.All.OrderBy(sp => sp.name.ToLowerInvariant()).Select(sp => sp.name.ToLowerInvariant());
#endif
                if (String.IsNullOrWhiteSpace(output))
                {
                    output = $"TP command Usage:\n/tp <place> \n\tor\n/tp <bookmark command> <place>[ \"<additional data>\"]";
                    output += $"\n\nDefault places:\n{WrappedLine($"{String.Join(", ", mappedValues)}")}";
                    output += $"\n\nBookmark commands:\n{WrappedLine(String.Join(", ", commands.Where(cmdGrp => cmdGrp.Commands[0] != "?").Select(cmdGrp => $"{String.Join("/", cmdGrp.Commands)}{(String.IsNullOrWhiteSpace(cmdGrp.Description) ? "" : $" - {cmdGrp.Description}")}")))}";
                    //output += $"\n\nNote: tp bookmark commands ending in an \"r\" indicate they save not only location, but also camera facing/rotation.";
                }
                else
                {
                    output += "\nUse \"/tp ?\" for help info";
                }
            }

            return (arg, output, wasCommand);
        }

        private static string WrappedLine(string input, bool indent = false)
        {
            uint maxLen = 55;
            string output = String.Empty;
            string rem = input;

            Logger.Debug($"Wrapping line: {rem}, with length: {(rem?.Length ?? 0)}");
            if (String.IsNullOrWhiteSpace(rem)) return rem;

            while ((uint)(rem?.Length ?? 0) > maxLen + 1)
            {
                var work = rem.Substring(0, (int)(maxLen));
                char[] rev = work.Reverse().ToArray();
                var enumerator = rev.GetEnumerator();
                var count = 0;
                char? previous = null;
                var matchA = " - ";
                var matchB = "Use: ";

                rem = rem.Substring((int)maxLen);

                Logger.Debug($"In outer loop, processing: {work}, rem: {rem}");
                if (work.Contains(", "))
                {
                    while (enumerator.MoveNext())
                    {
                        var currentChar = (char)enumerator.Current;
                        count++;

                        if (currentChar == ',' && previous == " "[0])
                        {
                            var offset = work.Length - count;
                            string next;

                            Logger.Debug($"offset: {offset}, work: {work}");
                            rem = work.Substring(offset + 2) + rem;
                            next = $"{(output == String.Empty ? "" : $"{(work.StartsWith(matchB) ? "\n\t" : ",\n")}")}{(indent ? "\t" : "")}{work.Substring(0, offset)}";
                            output += next;
                            Logger.Debug($"Adding {next} to output");
                            previous = null;
                            break;
                        }

                        previous = currentChar;
                    }
                }
                else if (rem.Contains(", "))
                {
                    var location = -1;

                    rem = work + rem;
                    location = rem.IndexOf(", ");
                    work = rem.Substring(0, location);
                    rem = rem.Substring(location + 2);

                    if (work.Length > maxLen)
                    {
                        if (work.Contains(matchA + matchB))
                        {
                            string work2;

                            location = work.IndexOf(matchA + matchB);
                            work2 = work.Substring(0, location);
                            rem = work.Substring(location + matchA.Length) + ", " + rem;

                            var next = $"{(output == String.Empty ? "" : $"{(work.StartsWith(matchB) ? "\n\t" : ",\n")}")}{(indent ? "\t" : "")}{work2}";
                            output += next;
                        }
                        else
                        {
                            if (work.Contains("/"))
                            {
                                var parts = work.Split('/');
                                var work2 = String.Join("/", parts.Take(parts.Length / 2));
                                string next;
                                
                                work = String.Join("/", parts.Skip((parts.Length / 2) + 1));
                                next = $"{(output == String.Empty ? "" : $"{(work.StartsWith(matchB) ? "\n\t" : ",\n")}")}{(indent ? "\t" : "")}{work}";
                                next = "\n{\n\t" + next;
                                output += next;
                                rem = "\t" + work + "\n}\n, " + rem;
                            }
                            else
                            {
                                var next = $"{(output == String.Empty ? "" : $"{(work.StartsWith(matchB) ? "\n\t" : ",\n")}")}{(indent ? "\t" : "")}{work}";
                                output += next;
                            }
                        }
                    }
                    else
                    {
                        var next = $"{(output == String.Empty ? "" : $"{(work.StartsWith(matchB) ? "\n\t" : ",\n")}")}{(indent ? "\t" : "")}{work}";
                        output += next;
                    }
                }
                else
                {
                    output += $"{(output == String.Empty ? "" : $"{(work.StartsWith(matchB) ? "\n\t" : ",\n")}")}{(indent ? "\t" : "")}{work + rem}";
                }
            }

            return $"{output}{(output == String.Empty ? "" : ",\n")}{(indent ? "\t" : "")}{rem}";
        }

        private static void WriteToFile(ref PatchState __state)
        {
            var additions = __state.Additions;

            if (writeWait != null)
            {
                Logger.Info("Cancelling pending save");
                writeCancel.Cancel();
            }

            try
            {
                Logger.Info($"attempting to save TP bookmarks");
                var appState = __state;
                Task cleanup = null;

                writeCancel = new CancellationTokenSource();
                writeWait = Task.Run(() => 
                { 
                    lock (lockObj) 
                    {
#if DEBUG
                        Logger.Info($"Writing file: {bookmarkPath}"); 
#endif
                        File.WriteAllText(bookmarkPath, JsonConvert.SerializeObject(additions));
#if DEBUG
                        Logger.Info("File write complete");
#endif
                    }
                }, writeCancel.Token);

                cleanup = writeWait.ContinueWith(t => 
                {
                    try
                    {
                        if (writeWait.IsFaulted)
                        {
                            Logger.Error(writeWait.Exception);
                            if (writeWait.Exception.InnerExceptions.Any()) foreach (var ex in writeWait.Exception.InnerExceptions) Logger.Info($"Inner exception: {ex}");
                            if (additions.Any(i => true)) foreach (var a in (IEnumerable<TPAddition>)additions) 
                            throw writeWait.Exception;
                        }
                        else if (!writeWait.IsCanceled)
                        {
                            appState.LastAdditionModified = DateTime.UtcNow;
                            Logger.Info("Finished saving TP bookmarks");
                        }
                    }
                    finally
                    {
                        writeWait = null;
                        writeCancel = null;
                        cleanup = null;
                    }
                });

                if (cleanup != null) Task.Delay(1000);
            }
            catch
            {
                writeWait = null;
                writeCancel = null;
                throw;
            }
        }

        private static string GetSerializedCoords(bool includeRotation = false)
        {
#if DEBUG
            var pos = new Vector3(0, 0, 0);
            var rot = new Quaternion(0, 0, 0, 0);
#else
            var pos = CameraSelector.shared.CurrentCameraPosition;
            Quaternion rot = Quaternion.identity;
#endif
            string output = $"{pos.x};{pos.y};{pos.z}";

            if (includeRotation) output += $";{rot.x};{rot.y};{rot.z};{rot.w}";

            return output;
        }

        private static (Vector3? location, Quaternion? rotation) ParseCoords(string coords)
        {
            Vector3? location = null;
            Quaternion? rotation = null;

            if (!String.IsNullOrWhiteSpace(coords))
            {
                var parts = coords.Trim().Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length > 2) location = new Vector3(Single.Parse(parts[0]), Single.Parse(parts[1]), Single.Parse(parts[2]));
                if (parts.Length > 6) rotation = new Quaternion(Single.Parse(parts[3]), Single.Parse(parts[4]), Single.Parse(parts[5]), Single.Parse(parts[6]));
            }
            else Logger.Info("Invalid coords for TP target");

            return (location, rotation);
        }
    }

    public class PatchState
    {
        public TPAdditions Additions;
        public string[] Args;
        public DateTime LastAdditionModified;
    }


    [JsonArray]
    public class TPAdditions : IEnumerable<KeyValuePair<uint, TPAddition>>, IEnumerable<TPAddition>
    {
        private SortedList<uint, TPAddition> contents = new SortedList<uint, TPAddition>();
        private readonly Serilog.ILogger logger = Log.ForContext<TeleportCommand>();

        public TPAdditions() { }
        public TPAdditions(IEnumerable<KeyValuePair<uint, TPAddition>> rawInput)
        {
            foreach (var item in rawInput) contents.Add(item.Key, item.Value);
        }

        public uint Count => (uint)contents.Keys.Count;

        IEnumerator<KeyValuePair<uint, TPAddition>> IEnumerable<KeyValuePair<uint, TPAddition>>.GetEnumerator()
        {
            using (var enumerator = contents.GetEnumerator())
            {
                while (enumerator.MoveNext()) yield return enumerator.Current;
            }
        }

        IEnumerator<TPAddition> IEnumerable<TPAddition>.GetEnumerator()
        {
            using (var enumerator = contents.GetEnumerator())
            {
                while (enumerator.MoveNext()) yield return enumerator.Current.Value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return (this as IEnumerable<KeyValuePair<uint, TPAddition>>).GetEnumerator();
        }

        public bool Any(Func<TPAddition, bool> @delegate)
        {
            return (this as IEnumerable<TPAddition>).Any(@delegate);
        }

        public TPAddition FirstOrDefault(Func<TPAddition, bool> @delegate)
        {
            foreach (var item in (this as IEnumerable<TPAddition>))
            {
                if (@delegate(item)) return item;
            }

            return default;
        }

        public void Remove(string key)
        {
            uint? keyId = contents.FirstOrDefault(c => c.Value.Keyword == key).Key;

            if (keyId != null) contents.Remove(keyId.Value);
        }

        public TPAddition this[string key]
        {
            get => contents.FirstOrDefault(c => c.Value.Keyword.ToLowerInvariant() == key).Value;
            set
            {
                var item = contents.FirstOrDefault(c => c.Value.Keyword.ToLowerInvariant() == key);

                logger.Info($"TPAdditions - {(item.Value?.Keyword == null ? "skipping removal of old entry" : $"removing old entry: {item.Value.Keyword}")}");
                if (!String.IsNullOrWhiteSpace(item.Value?.Keyword)) contents.Remove(item.Key);
                logger.Info($"TPAdditions' lastKey: {(contents.Count() > 0 ? contents.Last().Key : 0)}");
                if (value != null) contents.Add((contents.Count() > 0 ? contents.Last().Key + 1 : 0), value);
            }
        }

    }
    public class TPAddition
    {
        public string Keyword;
        public string AliasFor;
        public string Description;
        public string Coords;
    }
}