using System;
using System.Collections.Generic;
using System.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;

namespace EasyRedeemer;

/// <summary>
///   EasyRedeemer adds one command to ASF, "ezr" (long name: "ezredeem"):
///
///     ezr &lt;Bots&gt; &lt;AppID:Key&gt; [&lt;AppID:Key&gt; ...]    redeem keys
///     ezr file [&lt;Bots&gt;] &lt;path&gt;                      redeem keys from a file ("AppID Key" per line;
///                                                       "bots: bot1,bot2" lines choose the bots for the keys below them)
///     ezr failed [used|invalid|unusable|unexpected|rate|spare]         show the saved failed keys
///     ezr purge [yes [days]]                            delete saved failed keys (Owner only)
///
///   For every pair, the plugin first checks which of the selected bots already own the app.
///   The key is only tried on bots that do NOT own it.
///
///   Safety design: the ownership check is only an optimisation. If it is wrong, Steam answers AlreadyPurchased,
///   which does not consume the key, and the plugin simply moves on to the next bot.
///
///   Failed keys are kept in ONE file, config/EasyRedeemer-failed.txt, sorted by reason. A key that works later is
///   removed from it automatically.
///
///   NOTE: ASF is a trimmed build, so only plain, common framework calls are used on purpose.
/// </summary>
[Export(typeof(IPlugin))]
internal sealed class EasyRedeemer : IBotCommand2 {
	// Small pause between redeem attempts so we do not hammer Steam.
	private const int DelayBetweenAttemptsMs = 1500;

	private const string DateFormat = "yyyy-MM-dd HH:mm";

	private const string Usage =
		"Usage:\n" +
		"  ezr <Bots> <AppID:Key> [<AppID:Key> ...]\n" +
		"  ezr file [<Bots>] <path>\n" +
		"  ezr failed [used|invalid|unusable|unexpected|rate|spare]\n" +
		"  ezr purge [yes [days]]";

	// Reasons a key can end up in the failed-keys file, in the order they appear in it.
	private const string ReasonUsed = "Already used";
	private const string ReasonInvalid = "Invalid key";
	private const string ReasonUnusable = "Not usable on these bots";
	private const string ReasonUnexpected = "Unexpected response";
	private const string ReasonRate = "Not tried, rate limited";
	private const string ReasonSpare = "Not needed, still unused";

	private static readonly string[] ReasonOrder = { ReasonUsed, ReasonInvalid, ReasonUnusable, ReasonUnexpected, ReasonRate, ReasonSpare };

	private static readonly char[] Separators = { ' ', '\t', ',', ':', ';', '=' };
	private static readonly char[] LineSeparator = { '\n' };
	private static readonly char[] TabSeparator = { '\t' };

	// One file lock for everything that reads or writes the failed-keys file.
	private static readonly SemaphoreSlim FileLock = new(1, 1);

	// Relative to ASF's working directory, which is the folder that holds ASF and its "config" folder.
	private static string FailedFilePath => Path.Combine("config", "EasyRedeemer-failed.txt");

	public string Name => "EasyRedeemer";

	public Version Version => typeof(EasyRedeemer).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Command: ezr (or ezredeem) with file, failed, purge");

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		if (args.Length == 0) {
			return null;
		}

		string command = args[0].ToUpperInvariant();

		if (command is not ("EZR" or "EZREDEEM")) {
			return null; // Not our command, let ASF handle it
		}

		if (access < EAccess.Master) {
			return "Access denied: this command needs Master permission.";
		}

		if (args.Length < 2) {
			return Usage;
		}

		switch (args[1].ToUpperInvariant()) {
			case "FAILED":
				return await ShowFailed(args.Length > 2 ? args[2] : null).ConfigureAwait(false);

			case "PURGE":
				if (access < EAccess.Owner) {
					return "Access denied: purge needs Owner permission.";
				}

				return await Purge(args).ConfigureAwait(false);

			case "FILE":
				return await RedeemFromFile(access, args).ConfigureAwait(false);

			default:
				return await RedeemInline(access, args).ConfigureAwait(false);
		}
	}

	// ---------------------------------------------------------------- redeeming

	private static async Task<string> RedeemInline(EAccess access, string[] args) {
		if (args.Length < 3) {
			return Usage;
		}

		List<(uint AppID, string Key)> pairs = new();
		List<string> problems = new();

		for (int i = 2; i < args.Length; i++) {
			if (TryParsePair(args[i], out (uint AppID, string Key) pair)) {
				pairs.Add(pair);
			} else {
				problems.Add($"Could not read '{args[i]}' (expected AppID:Key, where the key has at least 10 letters, digits or hyphens).");
			}
		}

		return await Run(access, args[1], pairs, problems).ConfigureAwait(false);
	}

	private static async Task<string> RedeemFromFile(EAccess access, string[] args) {
		if (args.Length < 3) {
			return Usage;
		}

		// Two forms: "ezr file <path>" (bots come from "bots:" lines in the file)
		// and "ezr file <Bots> <path>" (the bots apply to keys above the first "bots:" line).
		// If everything after "file" is an existing file, it is the path; otherwise the first word is the bots.
		string? commandBots = null;
		string path = string.Join(' ', args.Skip(2));

		if (!File.Exists(path)) {
			if (args.Length < 4) {
				return $"File not found: {path}";
			}

			commandBots = args[2];
			path = string.Join(' ', args.Skip(3));

			if (!File.Exists(path)) {
				return $"File not found: {path}";
			}
		}

		// Keys are collected in groups. A new group starts whenever a "bots:" line changes the bots.
		List<(string Bots, List<(uint AppID, string Key)> Pairs)> groups = new();
		List<string> problems = new();
		string? currentBots = commandBots;

		string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);

		foreach (string line in content.Split(LineSeparator, StringSplitOptions.None)) {
			string trimmed = line.Trim();

			if ((trimmed.Length == 0) || (trimmed[0] == '#')) {
				continue;
			}

			if (trimmed.StartsWith("bots:", StringComparison.OrdinalIgnoreCase)) {
				string names = trimmed.Substring(5).Replace(" ", string.Empty).Replace("\t", string.Empty);

				if (names.Length == 0) {
					problems.Add("Empty 'bots:' line ignored.");
				} else {
					currentBots = names;
				}

				continue;
			}

			if (!TryParsePair(trimmed, out (uint AppID, string Key) pair)) {
				problems.Add($"Could not read line '{trimmed}'.");

				continue;
			}

			if (currentBots == null) {
				problems.Add($"No bots for '{trimmed}'. Add a 'bots:' line above it, or give bots in the command.");

				continue;
			}

			if ((groups.Count == 0) || (groups[groups.Count - 1].Bots != currentBots)) {
				groups.Add((currentBots, new List<(uint AppID, string Key)>()));
			}

			groups[groups.Count - 1].Pairs.Add(pair);
		}

		if (groups.Count == 0) {
			return "No valid AppID/Key pairs found." + (problems.Count > 0 ? "\n" + string.Join('\n', problems) : "");
		}

		StringBuilder all = new();

		foreach ((string groupBots, List<(uint AppID, string Key)> groupPairs) in groups) {
			string result = await Run(access, groupBots, groupPairs, new List<string>()).ConfigureAwait(false);

			if (groups.Count > 1) {
				AddLine(all, $"== bots: {groupBots} ==");
			}

			AddLine(all, result);

			if (groups.Count > 1) {
				AddLine(all, string.Empty);
			}
		}

		string text = all.ToString().TrimEnd();

		return problems.Count > 0 ? text + "\n" + string.Join('\n', problems) : text;
	}

	private static async Task<string> Run(EAccess access, string botSelector, List<(uint AppID, string Key)> pairs, List<string> problems) {
		HashSet<Bot>? bots = Bot.GetBots(botSelector);

		if ((bots == null) || (bots.Count == 0)) {
			return $"No bots found for '{botSelector}'.";
		}

		if (pairs.Count == 0) {
			return "No valid AppID/Key pairs found." + (problems.Count > 0 ? "\n" + string.Join('\n', problems) : "");
		}

		List<FailedEntry> failed = new();
		List<string> succeeded = new();

		string report = await RedeemAll(access, bots.OrderBy(static b => b.BotName, StringComparer.OrdinalIgnoreCase).ToList(), pairs, failed, succeeded).ConfigureAwait(false);

		string saveNote = await SaveFailures(failed, succeeded).ConfigureAwait(false);

		if (saveNote.Length > 0) {
			report += "\n" + saveNote;
		}

		return problems.Count > 0 ? report + "\n" + string.Join('\n', problems) : report;
	}

	// Plain Append calls on purpose: ASF is a trimmed build and lacks the interpolated-string StringBuilder overloads.
	private static void AddLine(StringBuilder sb, string line) {
		sb.Append(line);
		sb.Append('\n');
	}

	// Keys are at least 10 characters of letters, digits and hyphens. This only stops typos and stray words from being
	// sent to Steam as keys (each attempt counts towards Steam's hourly limit). It does not check the exact key format.
	private static bool LooksLikeKey(string text) {
		if (text.Length < 10) {
			return false;
		}

		foreach (char c in text) {
			bool ok = ((c >= 'a') && (c <= 'z')) || ((c >= 'A') && (c <= 'Z')) || ((c >= '0') && (c <= '9')) || (c == '-');

			if (!ok) {
				return false;
			}
		}

		return true;
	}

	private static bool TryParsePair(string text, out (uint AppID, string Key) pair) {
		pair = default;

		// Plain string.Split on purpose: ASF is a trimmed build, so rarely-used framework APIs (like Regex.Split) may be missing at runtime.
		string[] parts = text.Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

		if ((parts.Length != 2) || !uint.TryParse(parts[0], out uint appID) || (appID == 0) || !LooksLikeKey(parts[1])) {
			return false;
		}

		pair = (appID, parts[1]);

		return true;
	}

	private static async Task<string> RedeemAll(EAccess access, List<Bot> bots, List<(uint AppID, string Key)> pairs, List<FailedEntry> failed, List<string> succeeded) {
		StringBuilder report = new();
		List<(uint AppID, string Key)> pending = new();

		// Remember what we learned during this run, because ASF's own owned-package list can lag behind a fresh redeem.
		HashSet<(string BotName, uint AppID)> known = new();

		// Bots that cannot use a key for this app for a reason other than owning it (restricted country, missing base game...).
		HashSet<(string BotName, uint AppID)> unusable = new();

		bool rateLimited = false;

		foreach ((uint appID, string key) in pairs) {
			if (rateLimited) {
				pending.Add((appID, key));
				failed.Add(new FailedEntry(DateTime.Now, appID.ToString(CultureInfo.InvariantCulture), key, ReasonRate, string.Empty));

				continue;
			}

			List<Bot> candidates = bots.Where(b => b.IsConnectedAndLoggedOn && !known.Contains((b.BotName, appID)) && !unusable.Contains((b.BotName, appID)) && !OwnsApp(b, appID)).ToList();

			if (candidates.Count == 0) {
				if (bots.Exists(b => unusable.Contains((b.BotName, appID)))) {
					failed.Add(new FailedEntry(DateTime.Now, appID.ToString(CultureInfo.InvariantCulture), key, ReasonUnusable, "no selected bot can use this app (see earlier result)"));
					AddLine(report, $"app {appID}: not tried, no selected bot can use this app (key not used): {key}");
				} else {
					failed.Add(new FailedEntry(DateTime.Now, appID.ToString(CultureInfo.InvariantCulture), key, ReasonSpare, "every selected bot already owns this app"));
					AddLine(report, $"app {appID}: skipped, every selected bot already owns it (key not used, saved as spare): {key}");
				}

				continue;
			}

			bool finished = false;
			bool sawUnusable = false;
			string unusableNote = string.Empty;

			foreach (Bot candidate in candidates) {
				// "redeem^" with SF,SD,SAWK switches off forwarding, distributing and wallet-key handling for this one call,
				// whatever the bot's RedeemingPreferences say. Otherwise ASF could pass the key to another bot by itself.
				string? response = await candidate.Commands.Response(access, "redeem^ " + candidate.BotName + " SF,SD,SAWK " + key).ConfigureAwait(false);

				await Task.Delay(DelayBetweenAttemptsMs).ConfigureAwait(false);

				string appText = appID.ToString(CultureInfo.InvariantCulture);

				switch (Classify(response)) {
					case Outcome.Success:
						known.Add((candidate.BotName, appID));
						succeeded.Add(key);
						AddLine(report, $"app {appID}: ACTIVATED on {candidate.BotName} ({key})");
						finished = true;

						break;

					case Outcome.AlreadyOwned:
						// Not consumed. Remember it and try the next bot that might be missing the app.
						known.Add((candidate.BotName, appID));

						continue;

					case Outcome.RateLimited:
						rateLimited = true;
						pending.Add((appID, key));
						failed.Add(new FailedEntry(DateTime.Now, appText, key, ReasonRate, string.Empty));
						AddLine(report, $"app {appID}: Steam rate limit hit on {candidate.BotName}, stopping. Retry later.");
						finished = true;

						break;

					case Outcome.KeyUsed:
						failed.Add(new FailedEntry(DateTime.Now, appText, key, ReasonUsed, string.Empty));
						AddLine(report, $"app {appID}: key already used ({key}). Response: {Flatten(response)}");
						finished = true;

						break;

					case Outcome.KeyInvalid:
						failed.Add(new FailedEntry(DateTime.Now, appText, key, ReasonInvalid, string.Empty));
						AddLine(report, $"app {appID}: key invalid ({key}). Response: {Flatten(response)}");
						finished = true;

						break;

					case Outcome.NotUsable:
						// Not consumed. This bot cannot use the key (restricted country, missing base game, locked account...), so try the next one.
						unusable.Add((candidate.BotName, appID));
						sawUnusable = true;
						unusableNote = Flatten(response);

						continue;

					default:
						failed.Add(new FailedEntry(DateTime.Now, appText, key, ReasonUnexpected, Flatten(response)));
						AddLine(report, $"app {appID}: unexpected response on {candidate.BotName}, stopped trying this key ({key}). Response: {Flatten(response)}");
						finished = true;

						break;
				}

				if (finished) {
					break;
				}
			}

			if (!finished) {
				if (sawUnusable) {
					failed.Add(new FailedEntry(DateTime.Now, appID.ToString(CultureInfo.InvariantCulture), key, ReasonUnusable, unusableNote));
					AddLine(report, $"app {appID}: no selected bot could use this key, it was not consumed ({key}). Last response: {unusableNote}");
				} else {
					failed.Add(new FailedEntry(DateTime.Now, appID.ToString(CultureInfo.InvariantCulture), key, ReasonSpare, "every selected bot already owns this app"));
					AddLine(report, $"app {appID}: no selected bot could activate it, all reported already owning it (key not used, saved as spare): {key}");
				}
			}
		}

		if (pending.Count > 0) {
			AddLine(report, string.Empty);
			AddLine(report, "Not processed yet (run again later):");

			foreach ((uint appID, string key) in pending) {
				AddLine(report, $"{appID}:{key}");
			}
		}

		return report.ToString().TrimEnd();
	}

	/// <summary>
	///   VERIFY AGAINST YOUR ASF VERSION.
	///   Bot.OwnedPackages is a dictionary of owned package (license) IDs. ASF keeps the package -> app mapping in its
	///   global database. If this member name differs in your ASF version, only this method needs changing.
	///   A wrong "false" here is harmless (see class summary); a wrong "true" would skip a bot, so keep it strict.
	/// </summary>
	private static bool OwnsApp(Bot bot, uint appID) {
		if (ASF.GlobalDatabase == null) {
			return false;
		}

		foreach (uint packageID in bot.OwnedPackages.Keys) {
			if (ASF.GlobalDatabase.PackagesDataReadOnly.TryGetValue(packageID, out var data) && (data.AppIDs != null) && data.AppIDs.Contains(appID)) {
				return true;
			}
		}

		return false;
	}

	private enum Outcome : byte {
		Success,
		AlreadyOwned,
		RateLimited,
		KeyUsed,
		KeyInvalid,
		NotUsable,
		Unknown
	}

	// ASF prints Steam's status names (for example "Fail/AlreadyPurchased"). Those enum names are not localised.
	private static Outcome Classify(string? response) {
		if (string.IsNullOrWhiteSpace(response)) {
			return Outcome.Unknown;
		}

		if (response.Contains("RateLimited", StringComparison.OrdinalIgnoreCase)) {
			return Outcome.RateLimited;
		}

		// Only "OK/..." counts as success. "Fail/NoDetail" is a failure, so NoDetail alone must not be treated as success.
		if (response.Contains("OK/", StringComparison.Ordinal)) {
			return Outcome.Success;
		}

		if (response.Contains("AlreadyPurchased", StringComparison.OrdinalIgnoreCase)) {
			return Outcome.AlreadyOwned;
		}

		if (response.Contains("DuplicateActivationCode", StringComparison.OrdinalIgnoreCase)) {
			return Outcome.KeyUsed;
		}

		if (response.Contains("BadActivationCode", StringComparison.OrdinalIgnoreCase)) {
			return Outcome.KeyInvalid;
		}

		// ASF itself treats these as "this bot cannot use the key, another bot might" (the key is not consumed).
		if (response.Contains("DoesNotOwnRequiredApp", StringComparison.OrdinalIgnoreCase) ||
			response.Contains("RestrictedCountry", StringComparison.OrdinalIgnoreCase) ||
			response.Contains("AccountLocked", StringComparison.OrdinalIgnoreCase) ||
			response.Contains("NoWallet", StringComparison.OrdinalIgnoreCase) ||
			response.Contains("CancelledByUser", StringComparison.OrdinalIgnoreCase)) {
			return Outcome.NotUsable;
		}

		return Outcome.Unknown;
	}

	private static string Flatten(string? text) => string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();

	// ---------------------------------------------------------------- failed-keys file

	private sealed class FailedEntry {
		internal readonly string AppID;
		internal readonly string Key;
		internal readonly string Note;
		internal readonly string Reason;
		internal readonly DateTime Time;

		internal FailedEntry(DateTime time, string appID, string key, string reason, string note) {
			Time = time;
			AppID = appID;
			Key = key;
			Reason = reason;
			Note = note;
		}
	}

	private static bool IsReason(string title) {
		foreach (string reason in ReasonOrder) {
			if (reason == title) {
				return true;
			}
		}

		return false;
	}

	private static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	// File layout (header lines start at the left edge, key lines start with a tab):
	//   EasyRedeemer failed keys
	//   Updated: 2026-10-24 08:45
	//
	//   Already used (2)
	//   <tab>2026-10-24 08:35<tab>440<tab>AAAAA-BBBBB-CCCCC
	private static async Task<List<FailedEntry>> LoadFailed() {
		List<FailedEntry> list = new();

		if (!File.Exists(FailedFilePath)) {
			return list;
		}

		string reason = string.Empty;
		string content = await File.ReadAllTextAsync(FailedFilePath).ConfigureAwait(false);

		foreach (string rawLine in content.Split(LineSeparator, StringSplitOptions.None)) {
			string line = rawLine.Replace('\r', ' ');

			if (line.Trim().Length == 0) {
				continue;
			}

			if (line[0] == '\t') {
				string[] fields = line.Split(TabSeparator, StringSplitOptions.None); // fields[0] is empty (the leading tab)

				if ((reason.Length == 0) || (fields.Length < 4)) {
					continue;
				}

				if (!DateTime.TryParseExact(fields[1], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)) {
					time = DateTime.Now;
				}

				list.Add(new FailedEntry(time, fields[2], fields[3], reason, fields.Length > 4 ? fields[4] : string.Empty));

				continue;
			}

			string header = line.Trim();
			int paren = header.LastIndexOf(" (", StringComparison.Ordinal);
			string title = paren > 0 ? header.Substring(0, paren) : header;

			if (IsReason(title)) {
				reason = title;
			}
		}

		return list;
	}

	private static async Task SaveFailedFile(List<FailedEntry> entries) {
		if (entries.Count == 0) {
			if (File.Exists(FailedFilePath)) {
				File.Delete(FailedFilePath);
			}

			return;
		}

		// ASF's "config" folder always exists, so there is nothing to create here.
		StringBuilder sb = new();

		AddLine(sb, "EasyRedeemer failed keys");
		AddLine(sb, "Updated: " + DateTime.Now.ToString(DateFormat, CultureInfo.InvariantCulture));

		foreach (string reason in ReasonOrder) {
			List<FailedEntry> group = entries.Where(e => e.Reason == reason).OrderBy(static e => e.Time).ToList();

			if (group.Count == 0) {
				continue;
			}

			AddLine(sb, string.Empty);
			AddLine(sb, $"{reason} ({group.Count})");

			foreach (FailedEntry entry in group) {
				string line = "\t" + entry.Time.ToString(DateFormat, CultureInfo.InvariantCulture) + "\t" + entry.AppID + "\t" + entry.Key;

				if (entry.Note.Length > 0) {
					line += "\t" + entry.Note;
				}

				AddLine(sb, line);
			}
		}

		await WriteAtomicAsync(FailedFilePath, sb.ToString()).ConfigureAwait(false);
	}

	// Writes to a temporary file first, so a failure half-way cannot damage the existing file.
	// If replacing the old file with the temporary one is not possible, it falls back to writing the file directly.
	private static async Task WriteAtomicAsync(string path, string text) {
		string temp = path + ".tmp";

		await File.WriteAllTextAsync(temp, text).ConfigureAwait(false);

		bool moved;

		try {
			moved = TryMoveOver(temp, path);
		} catch (Exception) {
			moved = false;
		}

		if (!moved) {
			await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
			File.Delete(temp);
		}
	}

	// Kept in its own small method so that a missing framework method here cannot break the code around it.
	private static bool TryMoveOver(string temp, string path) {
		try {
			File.Move(temp, path, true);

			return true;
		} catch (Exception) {
			return false;
		}
	}

	// Merges this run's results into the file: new failures are added (replacing an older entry for the same key),
	// and keys that worked this time are removed.
	private static async Task<string> SaveFailures(List<FailedEntry> rawFailed, List<string> succeeded) {
		// One entry per key (the latest one), and never list a key that worked during this same run.
		List<FailedEntry> failed = new();

		foreach (FailedEntry entry in rawFailed) {
			if (succeeded.Exists(s => SameKey(s, entry.Key))) {
				continue;
			}

			failed.RemoveAll(x => SameKey(x.Key, entry.Key));
			failed.Add(entry);
		}

		if ((failed.Count == 0) && (succeeded.Count == 0)) {
			return string.Empty;
		}

		await FileLock.WaitAsync().ConfigureAwait(false);

		try {
			List<FailedEntry> all = await LoadFailed().ConfigureAwait(false);

			int fixedCount = all.Count(e => succeeded.Exists(s => SameKey(s, e.Key)));

			all.RemoveAll(e => succeeded.Exists(s => SameKey(s, e.Key)) || failed.Exists(f => SameKey(f.Key, e.Key)));
			all.AddRange(failed);

			if ((failed.Count == 0) && (fixedCount == 0)) {
				return string.Empty; // Nothing changed, leave the file alone
			}

			await SaveFailedFile(all).ConfigureAwait(false);

			StringBuilder note = new();

			int spareCount = failed.Count(f => f.Reason == ReasonSpare);
			int failCount = failed.Count - spareCount;

			if (failCount > 0) {
				AddLine(note, $"Saved {failCount} failed key(s) to {FailedFilePath}. Show them with: ezr failed");
			}

			if (spareCount > 0) {
				AddLine(note, $"Saved {spareCount} unused key(s) that no bot needed (still valid). Show them with: ezr failed spare");
			}

			if (fixedCount > 0) {
				AddLine(note, $"Removed {fixedCount} key(s) from the failed list because they worked this time.");
			}

			return note.ToString().TrimEnd();
		} catch (Exception e) {
			return $"Could not update the failed-keys file: {e.Message}";
		} finally {
			FileLock.Release();
		}
	}

	// ---------------------------------------------------------------- ezr failed

	private static async Task<string> ShowFailed(string? filter) {
		await FileLock.WaitAsync().ConfigureAwait(false);

		try {
			List<FailedEntry> all = await LoadFailed().ConfigureAwait(false);

			if (all.Count == 0) {
				return "No failed keys saved.";
			}

			if (filter == null) {
				string whole = await File.ReadAllTextAsync(FailedFilePath).ConfigureAwait(false);

				return whole.Replace('\r', ' ').TrimEnd();
			}

			string reason;

			switch (filter.ToUpperInvariant()) {
				case "USED":
					reason = ReasonUsed;

					break;

				case "INVALID":
					reason = ReasonInvalid;

					break;

				case "UNUSABLE":
					reason = ReasonUnusable;

					break;

				case "UNEXPECTED":
					reason = ReasonUnexpected;

					break;

				case "SPARE":
				case "UNUSED":
					reason = ReasonSpare;

					break;

				case "RATE":
				case "RATELIMITED":
					reason = ReasonRate;

					break;

				default:
					return $"Unknown filter '{filter}'. Use: used, invalid, unusable, unexpected, rate or spare.";
			}

			// Plain "AppID Key" lines, so the list can be pasted into a file and used with: ezr file <Bots> <path>
			StringBuilder sb = new();
			int count = 0;

			foreach (FailedEntry entry in all.Where(e => e.Reason == reason).OrderBy(static e => e.Time)) {
				AddLine(sb, entry.AppID + " " + entry.Key);
				count++;
			}

			return count == 0 ? $"No keys under '{reason}'." : sb.ToString().TrimEnd();
		} catch (Exception e) {
			return $"Could not read the failed-keys file: {e.Message}";
		} finally {
			FileLock.Release();
		}
	}

	// ---------------------------------------------------------------- ezr purge

	private static async Task<string> Purge(string[] args) {
		bool confirmed = (args.Length > 2) && (args[2].ToUpperInvariant() == "YES");
		int days = 0;

		if (confirmed && (args.Length > 3) && (!int.TryParse(args[3], out days) || (days <= 0))) {
			return "The number of days must be a whole number above 0, for example: ezr purge yes 7";
		}

		await FileLock.WaitAsync().ConfigureAwait(false);

		try {
			List<FailedEntry> all = await LoadFailed().ConfigureAwait(false);

			if (all.Count == 0) {
				return "No failed keys saved.";
			}

			if (!confirmed) {
				StringBuilder sb = new();

				AddLine(sb, $"{all.Count} failed key(s) saved:");

				foreach (string reason in ReasonOrder) {
					int count = all.Count(e => e.Reason == reason);

					if (count > 0) {
						AddLine(sb, $"  {reason}: {count}");
					}
				}

				AddLine(sb, "Nothing was deleted. Run 'ezr purge yes' to delete all of them, or 'ezr purge yes <days>' to delete only those older than that many days.");

				return sb.ToString().TrimEnd();
			}

			if (days == 0) {
				await SaveFailedFile(new List<FailedEntry>()).ConfigureAwait(false);

				return $"Deleted all {all.Count} failed key(s).";
			}

			DateTime cutoff = DateTime.Now.AddDays(-days);
			int before = all.Count;

			all.RemoveAll(e => e.Time < cutoff);
			await SaveFailedFile(all).ConfigureAwait(false);

			return $"Deleted {before - all.Count} failed key(s) older than {days} day(s). {all.Count} left.";
		} catch (Exception e) {
			return $"Could not purge the failed-keys file: {e.Message}";
		} finally {
			FileLock.Release();
		}
	}
}
