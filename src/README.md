# EasyRedeemer for ArchiSteamFarm

Redeems keys on many bots at once, and only tries a key on bots that do **not** already own the game's App ID. Keys that fail are saved in one file, sorted by reason.

## Install

Download and extract latest release and put it in plugins folder.

## Commands or try **[Command Builder](https://codepincer.github.io/EasyRedeemer/tools/commandbuilder.html)**

One command, `ezr` (long name: `ezredeem`):

```
ezr <Bots> <AppID:Key> [<AppID:Key> ...]     redeem keys
ezr file [<Bots>] <path>                     redeem keys from a file
ezr failed [used|invalid|unusable|unexpected|rate|spare]    show the saved failed keys
ezr purge [yes [days]]                       delete saved failed keys (Owner only)
```

`<Bots>` works like in other ASF commands: `bot1,bot2`, `asf`, `@ALL` and so on. Do not name a bot `file`, `failed` or `purge`.

Redeeming and `ezr failed` need **Master** permission (the ASF console counts as Owner). `ezr purge` needs **Owner**.

### Redeem

```
ezr asf 440:AAAAA-BBBBB-CCCCC 730:DDDDD-EEEEE-FFFFF
```

Write one `AppID:Key` per key. A comma also works (`440,KEY`), a space does not.

### Redeem from a file

```
ezr file asf /home/you/keys.txt
```

One pair per line, with a space, comma or colon between the two parts. `#` starts a comment:

```
440 AAAAA-BBBBB-CCCCC
730,DDDDD-EEEEE-FFFFF
```

#### Choosing the bots inside the file

A `bots:` line sets the bots for every key below it, until the next `bots:` line:

```
# keys for two bots
bots: bot1,bot2
440 AAAAA-BBBBB-CCCCC
730 DDDDD-EEEEE-FFFFF

# keys for one bot
bots: bot3
570 GGGGG-HHHHH-IIIII
```

Then you don't need to give bots in the command:

```
ezr file keys.txt
```

- If you do give bots in the command (`ezr file asf keys.txt`), they apply to the keys above the first `bots:` line, and a `bots:` line overrides them from that point on.
- Keys with no bots from either place are skipped, with a message, and nothing is redeemed for them.
- Files without any `bots:` line work exactly as before.
- If the file path contains spaces, `ezr file my keys.txt` still works. The plugin checks whether everything after `file` is an existing file before treating the first word as the bots.

## What happens for each pair

1. Picks the online bots that do not own the App ID.
2. Tries the key on the first one. On success, done.
3. If Steam says `AlreadyPurchased` (the key is not consumed), it tries the next bot.
4. If Steam says `RateLimited`, it stops and lists every pair not processed yet so you can run them later.
5. If Steam says this bot cannot use the key (`DoesNotOwnRequiredApp`, `RestrictedCountry`, `AccountLocked`, `NoWallet`, `CancelledByUser`), the key is not consumed and the next bot is tried. If no bot can use it, it is saved as "Not usable on these bots".
6. If every selected bot already owns the app, the key is not sent to Steam. It is saved as "Not needed, still unused" so you do not lose it. These keys are still valid. Show them as plain `AppID Key` lines with `ezr failed spare`.
7. Used keys, invalid keys and unexpected answers stop that key and are reported.

The ownership check is only an optimisation. If it ever guesses wrong, Steam's answer corrects it.

## Failed keys file

Keys that fail are saved in **one** file, `config/EasyRedeemer-failed.txt`, next to your other ASF configs. It is sorted by reason:

```
EasyRedeemer failed keys
Updated: 2026-10-24 08:45

Already used (2)
	2026-10-24 08:35	440	AAAAA-BBBBB-CCCCC
	2026-10-24 08:35	570	GGGGG-HHHHH-IIIII

Invalid key (1)
	2026-10-24 08:35	730	DDDDD-EEEEE-FFFFF

Unexpected response (1)
	2026-10-24 08:35	620	JJJJJ-KKKKK-LLLLL	Fail/Timeout

Not tried, rate limited (3)
	2026-10-24 08:36	440	MMMMM-NNNNN-OOOOO
```

- Every run merges into this file. A key that fails again moves to its new reason, and a key that **works** later is removed automatically.
- Keys skipped because every bot already owns the game are **not** failures, since those keys are still valid. They only appear in the command's reply.
- The file holds real keys in plain text. Keep it private, and delete it with `ezr purge yes` when you no longer need it.

### Show it

```
ezr failed             the whole file
ezr failed rate        only the rate-limited keys, as plain "AppID Key" lines
```

Filters: `used`, `invalid`, `unexpected`, `rate`. The filtered output has no timestamps, so you can paste it into a text file and run `ezr file <Bots> <path>` on it.

### Purge it

```
ezr purge              shows what is saved, deletes nothing
ezr purge yes          deletes everything
ezr purge yes 7        deletes only entries older than 7 days
```

## Build

1. Install the .NET SDK matching your ASF (see `"tfm"` in `ArchiSteamFarm.runtimeconfig.json` in your ASF folder, and set `TargetFramework` in the `.csproj` to the same value).
2. Build against the `ArchiSteamFarm.dll` from the **same ASF release** you run (the version must match exactly):

```
dotnet build -c Release -p:AsfDll="/full/path/to/ArchiSteamFarm.dll" -o out
```

3. Create `plugins/EasyRedeemer/` inside your ASF folder and copy `out/EasyRedeemer.dll` into it. Remove any older copy of this plugin first (the old `plugins/EasyRedeemerPlugin/` folder and its `EasyRedeemerPlugin.dll`).
4. Restart ASF. The log should show `EasyRedeemer 1.0.0 loaded`.

## Notes

- ASF's own build is trimmed, so some rarely used .NET methods are missing at runtime. The code sticks to plain, common calls on purpose. If you ever see `MissingMethodException`, report the method name.
- `OwnsApp(...)` uses `bot.OwnedPackages` and `ASF.GlobalDatabase.PackagesDataReadOnly`. Member names can differ between ASF versions. Only that method is affected.
- The failed-keys file path is relative to ASF's working directory (the folder that holds ASF and its `config` folder).
- The plugin redeems with `redeem^ <bot> SF,SD,SAWK <key>`, which switches off forwarding, distributing and wallet-key handling for each call. A bot's `RedeemingPreferences` therefore do not change how `ezr` works. They still apply to ASF's own `redeem` command.
- Test with one bot and one key first, and keep a copy of your key list.
