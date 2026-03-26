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
using UMM = UnityModManagerNet.UnityModManager;

namespace jjerhub.TPBookmarks
{
    public static class Logger
    {
        const string prefix = "[jjerhub.TPBookmarks] ";

        public static void Debug(string msg)
        {
#if DEBUG
            UMM.Logger.Log($"{prefix}{msg}");
#endif
        }

        public static void Info(string msg)
        {
            UMM.Logger.Log($"{prefix}{msg}");
        }

        public static void Error(string msg)
        {
            UMM.Logger.Error($"{prefix}{msg}");
        }
    }

    public class CommandList
    {
        public string[] Commands;
        public string Description;
    }

    public class TeleportCommand
    {
        static Harmony harmony = new Harmony("com.jjerhub.TPBookmarks");
        public static bool enabled;

        public static bool ApplyPatch(UMM.ModEntry modEntry)
        {
            modEntry.OnToggle = (entry, value) =>
            {
                if (value)
                {
                    if (!enabled)
                    {
                        harmony.PatchAll();
                        enabled = true;
                    }
                }
                else
                {
                    if (enabled)
                    {
                        harmony.UnpatchAll(harmony.Id);
                        enabled = false;
                    }
                }
                return true;
            };
            return true;
        }
    }

    [HarmonyPatch(typeof(UI.Console.Commands.TeleportCommand), "Execute")]
    internal static class TeleportCommandPatch
    {
        internal static readonly string asmPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        internal static readonly string bookmarkPath = Path.Combine(asmPath, "bookmarks.json");
        private static readonly bool showDescription = true; //TODO: make this a setting
        private static readonly object lockObj = new object();
        private static Task writeWait = null;
        private static CancellationTokenSource writeCancel = null;
        private static uint maxAliasDepth = 3;

        public static void Init() { }

        [HarmonyPrefix]
        public static void BeginTeleport(ref PatchState __state, string[] comps)
        {
            Init();

            Logger.Debug($"Setting state");
            __state = new PatchState() { Args = new List<string>(comps).ToArray() };

            //interrupt normal TP if we have changed it's location
            try { if (comps.Length >= 2) if (__state.Additions?.Any(a => a.Keyword() == comps[1].Trim().ToLowerInvariant()) ?? false) comps[1] = "TPBookmarkPlaceholder"; }
            finally { }
        }

        [HarmonyPostfix]
        public static void EndTeleport(ref PatchState __state, ref string __result)
        {
            Init();

            Logger.Debug($"Result before patching: \"{__result}\"");
            if (__result == "nothing") return;

            if (!__result.StartsWith("Follow"))
            {
                try
                {
                    Logger.Debug("Checking for custom TP bookmark");
                    if (__result.StartsWith("Jump"))
                    {
                        __result = "Jumped" + __result.Substring(4);
                    }
                    else
                    {
                        PollAdditions(__state);

                        Logger.Debug("Running custom TP parse routine");
                        var (arg, newOutput, wasCommand) = ValidateAndCommand(ref __state, bookmarkPath);

                        if (!__result.StartsWith("Jump") && !__result.StartsWith("Follow"))
                        {
                            Logger.Debug("Running custom TP handler");
                            if (String.IsNullOrWhiteSpace(newOutput) && !wasCommand) newOutput = HandleTP(ref __state, ref __result, arg);

                            __result = newOutput;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in TP bookmark patch: {ex}");
                }
                finally
                {
                    __state = default;
                }
            }
        }

        internal static void PollAdditions(PatchState __state)
        {
            var fileInfo = new FileInfo(bookmarkPath);

            if (fileInfo.Exists)
            {
                if (fileInfo.LastWriteTime > __state.LastAdditionModified)
                {
                    try
                    {
                        Logger.Debug("Polling additions from file");
                        var originalFormatAdditions = JsonConvert.DeserializeObject<IEnumerable<KeyValuePair<uint, TPAddition>>>(File.ReadAllText(bookmarkPath));

                        TPAdditions fromDisk;

                        if (originalFormatAdditions?.Any(a => a.Value?.Keyword() != null) ?? false) fromDisk = TPAdditions.Make(originalFormatAdditions);
                        else fromDisk = JsonConvert.DeserializeObject<TPAdditions>(File.ReadAllText(bookmarkPath));

                        if (fromDisk?.Any(a => a.Keyword() != null) ?? false)
                        {
                            Logger.Debug($"Found {fromDisk.Count()} additions in file, merging with state");
                            foreach (var addition in fromDisk) if (addition != null && addition._Keyword != null) __state.Additions[addition.Keyword()] = addition.Clone();
                            __state.LastAdditionModified = DateTime.Now;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error polling TP additions: {ex}");
                    }
                }
            }
            else
            {
                Logger.Info("No TP bookmark file found, starting with blank slate");
            }
        }

        private static string HandleTP(ref PatchState __state, ref string formerOutput, string _arg)
        {
            var arg = _arg.Trim().ToLowerInvariant();
            TPAddition addition = __state.TPPoints()?.FirstOrDefault(a => a.Keyword() == arg);

            Logger.Debug($"Handling TP to: \"{_arg}\"");
            if (addition != null && (!String.IsNullOrWhiteSpace(addition?.Coords) || !String.IsNullOrWhiteSpace(addition?.AliasFor())))
            {
                Vector3? location = null;
                Quaternion? rotation = null;
                TPAddition thisAddition = addition ?? default;

                Logger.Debug($"Checking for alias redirect for \"{_arg}\"");
                if (String.IsNullOrWhiteSpace(thisAddition.AliasFor())) (location, rotation) = ParseCoords(addition.Coords);
                else
                {
                    var original = thisAddition.Clone();
                    try 
                    {
                        var results = (set: (HashSet<TPAddition>)null, end: (TPAddition)null);
                        PeekAlias(__state, thisAddition, ref results);

                        thisAddition = results.end;
                        return HandleTP(ref __state, ref formerOutput, thisAddition.Keyword());
                    }
                    catch (Exception ex)
                    {
                        var msg = $"Unable to handle TP for: \"{original._Keyword}\".";
#if DEBUG
                        Logger.Error($"{msg} {ex.ToString()}");
#endif
                        return msg;
                    }
                }

                if (location != null)
                {
                    var camera = CameraSelector.shared;

                    if (rotation == null) rotation = Quaternion.identity;

                    if (camera != null)
                    {
                        camera.JumpToPoint(location.Value, rotation.Value);

                        return $"Jumped to {thisAddition._AliasFor ?? thisAddition._Keyword}";
                    }
                    else
                    {
                        return "No camera found to jump to location";
                    }
                }
                else
                {
#if DEBUG
                    Logger.Error($"Unable to parse location for TP target: \"{_arg}\"");
#endif
                }
            }
            else
            {
                Logger.Error($"Unable to TP to: \"{_arg}\"");
            }

            return formerOutput;
        }

        private static void PeekAlias(PatchState __state, TPAddition addition, ref (HashSet<TPAddition> set, TPAddition end) results, ushort depth = 0)
        {
            if (results.set == null) results.set = new HashSet<TPAddition>() { addition };
            else results.set.Add(addition);
            
            results.end = addition;

            if (!String.IsNullOrWhiteSpace(addition.AliasFor()))
            {
                TPAddition targetAddition = __state.TPPoints().FirstOrDefault(a => a.Keyword() == addition.AliasFor());

                if (targetAddition != null)
                {
                    Logger.Debug($"Found alias target: \"{targetAddition.Keyword()}\" for alias: \"{addition.Keyword()}\"");
                    if (!String.IsNullOrWhiteSpace(targetAddition.AliasFor()) && depth <= maxAliasDepth)
                    {
                        PeekAlias(__state, targetAddition, ref results, ++depth);
                    }
                    else if (depth > maxAliasDepth)
                    {
                        throw new Exception($"Max alias recursion depth reached");
                    }
                    else
                    {
                        results.end = targetAddition;
                    }
                }
                else
                {
                    throw new Exception($"Unable to find target for alias: \"{results.end._AliasFor}\"");
                }
            }
        }

        private static (string arg, string output, bool wasCommand) ValidateAndCommand(ref PatchState __state, string bookmarkPath)
        {
            var commands = new CommandList[]{
                new CommandList(){ Commands = new string[]{ "add" }, Description = "Use: /tp <command> <target>[ \"<description>]\"" }, 
                new CommandList(){ Commands = new string[]{ "update", "upd" }, Description = "Use: /tp <command> <target>[ \"<description>\"]" },
                new CommandList(){ Commands = new string[]{ "remove", "rm" }, Description = "Use: /tp <command> <target>" }, 
                new CommandList(){ Commands = new string[]{ "?" } }, 
                new CommandList(){ Commands = new string[]{ "alias", "aka" }, Description = "Use: /tp <command> <original> <alias>[ \"<description>\"]" }, 
                new CommandList(){ Commands = new string[]{ "rename", "rn" }, Description = "Use: /tp <command> <oldName> <newName>" },
                new CommandList(){ Commands = new string[]{ "info", "list", "ls" }, Description = "Use: /tp list[ <target>]" },
            };
            string output = String.Empty;
            string command = null;
            string _arg = null;
            Func<string> arg = () => _arg?.ToLowerInvariant();
            var args = __state.Args;
            bool wasCommand = false;

            if (args.Length > 1 && args.Length <= 5 && commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => args[1].ToLowerInvariant() == cmd)))
            {
                TPAddition targetItem = null;
                TPAddition otherItem = null;

                if (args.Length > 2 && commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => args[2].Trim('\"').ToLowerInvariant() == cmd))) output = $"\"{args[2].Trim('\"')}\" is an invalid name for a teleport target because it is a reserved word.";

                wasCommand = true;
                command = args[1].ToLowerInvariant();

                //populate vars
                Logger.Debug("Populating command variables");
                if (args.Length > 2)
                {
                    _arg = args[2].Trim('\"');
                    targetItem = new TPAddition() { _Keyword = _arg };

                    if (args.Length > 3)
                    {
                        if (commands[0].Commands.Contains(command)) //add
                        {
                            targetItem.Description = args[3].Trim('\"');
                        }
                        else if (commands[4].Commands.Contains(command)) //alias
                        {
                            otherItem = new TPAddition
                            {
                                _AliasFor = args[3]?.Trim('\"'),
                                _Keyword = targetItem._Keyword
                            };

                            if (args.Length > 4) otherItem.Description = args[4]?.Trim('\"');
                        }
                        else //update || rename
                        {
                            otherItem = new TPAddition
                            {
                                _Keyword = args[3]?.Trim('\"')
                            };

                            if (args.Length > 4) otherItem.Description = args[4]?.Trim('\"');
                        }
                    }
                }

                Logger.Debug("Populating target info");
                if (targetItem?.Keyword() != null && (__state.TPPoints()?.Any(a => a.Keyword() == targetItem.Keyword()) ?? false)) 
                    targetItem = __state.TPPoints().First(a => a.Keyword() == targetItem.Keyword());

                //TODO: make "commands" not order dependent
                if (commands[0].Commands.Contains(command)) //add
                {
                    if 
                    (
                            ((IEnumerable<TPAddition>)__state.TPPoints())?.Any(a => a.Keyword() == arg()) ?? false
                    )
                        output = $"\"{_arg}\" is already listed as a keyword.  You must specify a keyword that doesn't already exist, or use the \"update\" command.\n";
                    else if (commands.Any(cg => cg.Commands.Any(c => arg() == c))) 
                        output = $"\"{_arg}\" is an invalid name for a teleport target because it is a reserved word.";
                    else if (_arg == null) 
                        output = "You must specify a TP target when using the add command";
                    else
                    {
                        Logger.Debug("Attempting to add new TP location");
                        using (var scope = new TransactionScope())
                        {
                            targetItem.Coords = GetSerializedCoords(command[command.Length - 1] == 'r');

                            __state.Additions.Add(targetItem.Clone());

                            WriteToFile(__state.Clone());
                            output = $"Successfully added new TP target: \"{_arg}\"";

                            scope.Complete();
                        }
                    }
                }
                else if (commands[1].Commands.Contains(command)) //update
                {
                    Logger.Debug("Attempting to update TP location");
                    using (var scope = new TransactionScope())
                    {
                        Logger.Debug($"Checking for existing TP target with keyword: \"{targetItem._Keyword}\"");
                        if ((__state.TPPoints())?.Any(a => a?.Keyword() == targetItem.Keyword()) ?? false)
                        {
                            var wasError = false;
                            var original = targetItem.Clone();

                            Logger.Debug($"Found existing TP target for keyword: \"{targetItem._Keyword}\". Attempting data update");
                            if (otherItem?.Description == null) //if we have description, we know coords aren't changing, so no need to dig through alias
                            {
                                Logger.Debug($"Attempting to peek alias for: \"{original._Keyword}\" before update");
                                try
                                {
                                    var results = (set: (HashSet<TPAddition>)null, end: (TPAddition)null);
                                    PeekAlias(__state, targetItem, ref results);

                                    targetItem = results.end;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Debug($"Error peeking alias for: \"{original._Keyword}\". {ex.ToString()}");
                                    output = $"Unable to update TP target for: \"{original._Keyword}\".";
                                    wasError = true;
                                }

                                Logger.Debug($"Updating coords for: \"{original._Keyword}\"");
                                if (!wasError) targetItem.Coords = GetSerializedCoords(command[command.Length - 1] == 'r');
                                otherItem = targetItem.Clone();
                            }
                            else if (otherItem?.Description == "null") otherItem.Description = null;

                            if (!wasError)
                            {
                                if (otherItem.IsSpawn) otherItem.IsSpawn = false;

                                __state.Additions[otherItem.Keyword()] = otherItem.Clone();
                                WriteToFile(__state.Clone());
                                output = $"Successfully updated TP target: \"{original._Keyword}\"";
                            }
                        }
                        else if (_arg == null) 
                            output = "You must specify a TP target when using the update command";
                        else 
                            output = $"Unable to find \"{_arg}\" listed as a keyword. Perhaps you should \"add\" it?";

                        scope.Complete();
                    }
                }
                else if (commands[2].Commands.Contains(command)) //remove
                {
                    using (var scope = new TransactionScope())
                    {
                        if (((IEnumerable<TPAddition>)__state.Additions)?.Any(a => a.Keyword() == arg()) ?? false)
                        {
                            __state.Additions.Remove(targetItem.Clone());
                            WriteToFile(__state.Clone());
                            output = $"Successfully removed TP target: \"{_arg}\"";
                        }
                        else if (_arg == null) 
                            output = "You must specify a custom TP target when using the remove command";
                        else 
                            output = $"Unable to find \"{_arg}\" listed as a keyword. Perhaps you should \"add\" it?";

                        scope.Complete();
                    }
                }
                else if (commands[4].Commands.Contains(command)) //alias
                {
                    using (var scope = new TransactionScope())
                    {
                        if (otherItem?.AliasFor() == null)
                            output = "Existing TP target cannot be blank when using the alias command";
                        else if (String.IsNullOrWhiteSpace(otherItem.Keyword()))
                            output = "You must specify an alias name when using the alias command";
                        else if
                            (
                                !(__state.TPPoints()?.Any(a => a.Keyword() == otherItem.AliasFor()) ?? false)
                            )
                            output = "You must specify an existing target in order to create an alias";
                        else if (commands.Any(cg => cg.Commands.Any(c => c == otherItem.Keyword())))
                            output = $"\"{otherItem._Keyword}\" is an invalid name for a teleport target because it is a reserved word.";
                        else
                        {
                            var original = otherItem.Clone();
                            var tmpSet = new HashSet<TPAddition>();
                            tmpSet.Add(otherItem);
                            var results = (set: tmpSet, end: otherItem);

                            try
                            {
                                PeekAlias(__state, otherItem, ref results);

                                if (results.set.Any(a => a.AliasFor() == otherItem.Keyword()))
                                    output = $"Unable to set \"{otherItem._Keyword}\" as an alias of itself.";
                                else
                                {
                                    otherItem.Description = otherItem.Description == "null" ? null : otherItem.Description;

                                    Logger.Debug("Attempting write alias for: " + original._AliasFor);
                                    __state.Additions[otherItem.Keyword()] = otherItem.Clone();
                                    WriteToFile(__state.Clone());
                                    output = $"Successfully set TP target: \"{original._Keyword}\" as an alias for: \"{otherItem._AliasFor}\"";
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex.ToString());
                                output = $"Unable to find target item for alias creation from: \"{original._Keyword}\".  Unable to find: \"{results.end._AliasFor}\"";
                            }
                            finally
                            {
                                scope.Complete();
                            }
                        }
                    }
                }
                else if (commands[5].Commands.Contains(command)) //rename
                {
                    using (var scope = new TransactionScope())
                    {
                        if (targetItem._Keyword == null)
                            output = "You must specify a TP target to rename";
                        else if (commands.Any(cg => cg.Commands.Any(c => c == targetItem.Keyword()))) 
                            output = $"\"{targetItem._Keyword}\" is an invalid name for a teleport target because it is a reserved word.";
                        else if (targetItem.IsSpawn)
                            output = "Unable to rename spawn points.";
                        else
                        {
                            if (targetItem?.Keyword == null)
                                output = $"Unable to rename \"{targetItem._Keyword}\" to \"{targetItem._Keyword}\". Unable to find a teleport target for \"{targetItem._Keyword}\".";
                            else
                            {
                                var newItem = targetItem.Clone();
                                var tmpSet = new HashSet<TPAddition>();
                                tmpSet.Add(targetItem);
                                var results = (set: tmpSet, end: targetItem);
                                newItem._Keyword = otherItem._Keyword;
                                newItem.Description = otherItem.Description == "null" ? null : otherItem.Description;

                                try 
                                { 
                                    PeekAlias(__state, targetItem, ref results);

                                    if (results.set.Any(a => a.AliasFor() == newItem.Keyword()) || targetItem.AliasFor() == newItem.Keyword())
                                        output = $"Unable to rename \"{targetItem._Keyword}\" to \"{newItem._Keyword}\" because this target would become an alias of itself.";
                                    else
                                    {
                                        __state.Additions[targetItem.Keyword()] = newItem.Clone();
                                        WriteToFile(__state.Clone());
                                        output = $"Successfully renamed TP target: \"{targetItem._Keyword}\" to: \"{newItem._Keyword}\"";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error(ex.ToString());
                                    output = $"Unable to rename \"{targetItem._Keyword}\". Unable to determine alias recursion."; 
                                }
                            }
                        }

                        scope.Complete();
                    }
                }
                else if (commands[6].Commands.Contains(command)) //info/list
                { 
                    if (_arg == null)
                    {
                        var rendered = __state.TPPoints().Select
                            (
                                a => $"{a._Keyword}{(!showDescription ? "" : (String.IsNullOrWhiteSpace(a.Description) ? "" : $" - {a.Description}"))}"
                            );
                        output = $"Mapped TP targets:\n{WrappedLine(String.Join(", ", rendered))}";
                    }
                    else
                    {
                        var found = __state.TPPoints().FirstOrDefault(a => a.Keyword() == arg());

                        if (found != null)
                        {
                            output = $"Found: {found._Keyword}{(found.Description != null ? $" - {found.Description}" : "")}";
                        }
                        else
                        {
                            output = $"TP bookmark: {_arg} doesn't exist";
                        }
                    }
                }
            }
            else if (args.Length > 1 && args.Length < 3)
            {
                _arg = args[1];
            }

            Logger.Debug($"checking state before final error check. arg: \"{_arg}\", cmd: \"{command}\", output: \"{output}\"");
            if (args.Length > 0
                &&
                (
                    (String.IsNullOrWhiteSpace(arg()) && !commands[6].Commands.Contains(command)) //see if its a list/info command
                    || arg() == "?"
                    || (
                        !String.IsNullOrWhiteSpace(command) 
                        && commands.Any(cmdGrp => cmdGrp.Commands.Any(cmd => command == cmd)) 
                        && String.IsNullOrWhiteSpace(arg()) 
                        && !commands[6].Commands.Contains(command)
                       )
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
                var mappedValues = SpawnPoint.All.OrderBy(sp => sp.name.ToLowerInvariant())?.Select(sp => sp.name.ToLowerInvariant());
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

            return (arg(), output, wasCommand);
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

        private static void WriteToFile(PatchState __state)
        {
            var additions = __state.Additions.Clone();

            if (writeWait != null)
            {
                Logger.Debug("Cancelling pending save");
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
                        Logger.Debug($"Writing file: {bookmarkPath}");
                        File.WriteAllText(bookmarkPath, JsonConvert.SerializeObject(additions));
                        Logger.Debug("File write complete");
                    }
                }, writeCancel.Token);

                cleanup = writeWait.ContinueWith(t => 
                {
                    try
                    {
                        if (writeWait.IsFaulted)
                        {
                            Logger.Error(writeWait.Exception.ToString());
                            if (writeWait.Exception.InnerExceptions.Any()) foreach (var ex in writeWait.Exception.InnerExceptions) Logger.Error($"Inner exception: {ex}");
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

        internal static string GetSerializedCoords(bool includeRotation = false)
        {
#if LOCAL
            var pos = new Vector3(0, 0, 0);
            var rot = new Quaternion(0, 0, 0, 0);
#else
            var pos = CameraSelector.shared.CurrentCameraPosition;
            Quaternion rot = Quaternion.identity;
#endif
            return GetSerializedCoords((pos, rot), includeRotation);
        }

        internal static string GetSerializedCoords((Vector3 position, Quaternion rotation) coordObj, bool includeRotation = false)
        {
            var pos = coordObj.position;
            var rot = coordObj.rotation;
            string output = $"{pos.x};{pos.y};{pos.z}";

            if (includeRotation) output += $";{rot.x};{rot.y};{rot.z};{rot.w}";

            return output;
        }

        internal static (Vector3? location, Quaternion? rotation) ParseCoords(string coords)
        {
            Vector3? location = null;
            Quaternion? rotation = null;

            if (!String.IsNullOrWhiteSpace(coords))
            {
                var parts = coords.Trim().Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length > 2) location = new Vector3(Single.Parse(parts[0]), Single.Parse(parts[1]), Single.Parse(parts[2]));
                if (parts.Length > 6) rotation = new Quaternion(Single.Parse(parts[3]), Single.Parse(parts[4]), Single.Parse(parts[5]), Single.Parse(parts[6]));
            }
            else
            {
#if DEBUG
                Logger.Error("Invalid coords for TP target");
#endif
            }

            return (location, rotation);
        }
    }

    internal class PatchState
    {
        internal TPAdditions Additions = new TPAdditions();
        public string[] Args = new string[0];
        public DateTime LastAdditionModified = DateTime.MinValue;

        public PatchState()
        {
            TeleportCommandPatch.PollAdditions(this);
        }

        public TPAdditions TPPoints()
        {
            var result = Additions.Clone();

#if LOCAL
            result.UnionWith(new TPAddition[] {
                new TPAddition(){ _Keyword = "test me", Coords = "0;0;0", IsSpawn=true }
            });
#else
            var spawns = SpawnPoint.All
                    .Where(sp => !result.Any(a => a.Keyword() == sp.name.Trim().ToLowerInvariant()))
                    .Select(sp => new TPAddition() { _Keyword = sp.name, Coords = TeleportCommandPatch.GetSerializedCoords(sp.GamePositionRotation), IsSpawn = true });

            if (spawns.Any())
                result.UnionWith(spawns);

#endif
            return result;
        }

        public PatchState Clone()
        {
            return new PatchState()
            {
                Additions = Additions.Clone(),
                Args = Args,
                LastAdditionModified = LastAdditionModified
            };
        }
    }


    [JsonArray]
    internal class TPAdditions : HashSet<TPAddition>
    {
        public TPAdditions() : base(new TPAddition()) { }

        public TPAdditions Clone()
        {
            var newSet = new TPAdditions();
            
            newSet.UnionWith(this);

            return newSet;
        }

        public void Remove(string key)
        {
            var item = this.FirstOrDefault(a => a.Keyword() == key);
            if (item != null) this.Remove(item);
        }

        public TPAddition this[string key]
        {
            get => this.FirstOrDefault(a => a.Keyword() == key);
            set
            {
                using (var scope = new TransactionScope())
                {
                    this.Remove(key);
                    this.Add(value);
                    scope.Complete();
                }
            }
        }

        public static TPAdditions Make(IEnumerable<KeyValuePair<uint, TPAddition>> rawAdditions)
        {
            var additions = new TPAdditions();

            additions.UnionWith(rawAdditions.Where(kvp => kvp.Value != null).Select(kvp => kvp.Value.Clone()));

            return additions;
        }
    }

    internal class TPAddition : IEqualityComparer<TPAddition>, IEquatable<TPAddition>
    {
        [JsonProperty("Keyword")]
        internal string _Keyword;
        [JsonProperty("AliasFor")]
        internal string _AliasFor;
        [JsonIgnore]
        public Func<string> Keyword;
        [JsonIgnore]
        public Func<string> AliasFor;
        public string Description;
        public string Coords;
        public bool IsSpawn = false;

        public TPAddition()
        {
            Keyword = () => _Keyword.Trim().ToLowerInvariant();
            AliasFor = () => _AliasFor?.Trim().ToLowerInvariant();
        }

        public TPAddition Clone()
        {
            return new TPAddition()
            {
                _Keyword = _Keyword,
                _AliasFor = _AliasFor,
                Description = Description,
                Coords = Coords
            };
        }

        public bool Equals(TPAddition other)
        {
            return _Keyword == other._Keyword;
        }

        public bool Equals(TPAddition x, TPAddition y) => x.Equals(y);

        public int GetHashCode(TPAddition obj) => obj._Keyword.GetHashCode();
    }
}