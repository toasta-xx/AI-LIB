# NullAgent

A voice assistant you can drop into any BepInEx mod menu.

Everything runs on the player's own PC: the language model, the speech recognition, and
the audio. No API key, no cloud service, nothing sent off the machine.

You say a wake word, ask for something, and it does it. "turn off fly" flips the Fly mod.  
"play hell by streetlight manifesto" opens the song in YouTube Music. "search who invented the telephone"  
reads back a short answer.

Chat is handled by a local Llama 3.2 3B model through llama.cpp. Speech recognition uses
whisper.cpp. Both are free and both are downloaded once.

Free to use in open source and closed source projects. Credit **Toast** and link the
repository. [LICENSE](LICENSE) · [TRADEMARK.md](TRADEMARK.md)

---

## Installing it as a player

1. Download `NullAgent.dll`.
2. Put the file in `BepInEx/plugins/`.
3. Start the game.

On the first launch it downloads about 2.1 GB into `BepInEx/NullAgentLib/`. A line appears
on screen showing progress while this happens. It only happens once. Every launch after
that reads the files from disk and starts right away.

If you already have that folder from a previous install, copy it into the new BepInEx
folder and nothing will download.

You do not need to install anything else. No account, no extra programs.

### Talking to it

Wake it with any of these words:

`jarvis` `null` `mrgpt` `mr gpt` `mister gpt` `chatgpt` `chat gpt` `chatgbt`

Then say what you want, like "hey null turn off fly" or
"jarvis play bohemian rhapsody". If the menu you are using sets a custom name for the
agent, that name also works as a wake word.

The agent can:

- turn menu entries/mods on and off
- play, pause, skip, mute, and change the volume of music
- look up short facts
- open a web page

It will not find every mod. Menus with unusual layouts or entries that do not have a clear
on and off state may not resolve. When that happens the agent says it could not find it,
and you can still click the mod by hand.

---

## Adding it to your mod

If you are building a menu and want the agent inside it, there are three steps.

**1. Copy the** `AI LIB` **folder into your project.**

**2. Add a project reference** to your `.csproj`:

```xml
<ProjectReference Include="AI LIB\NullAgent.csproj" />
```

**3. Call** `Attach` **once**, anywhere after the player has spawned:

```csharp
NullAgent.Attach(this);
```

Pass in a `MonoBehaviour` you own. The library creates its own object underneath it, starts
the model, starts the microphone, and shuts everything down when the scene unloads.

That is the whole required setup. The rest of this page is optional.

### Changing the name and prompt

```csharp
NullAgent.Configure(c =>
{
    c.AgentName = "Atlas";
    c.SystemPrompt = "You are Atlas, the assistant inside my menu. Keep replies under two sentences.";
});
```

Setting `AgentName` changes the prefix on notifications, the name used in the spawn
greeting, and it becomes a wake word automatically. You do not have to register it.

### Using your own notifications

The library draws a small label in the corner by default. To route text into your  
own HUD instead:

```csharp
NullAgent.Configure(c => c.Notification = line => MyHud.Show(line));
```

The string is plain text and may contain rich text tags for color.

### All settings

Every setting is optional.

| Setting | Default | What it does |
|---|---|---|
| `AgentName` | `NullAgent` | Prefix on notifications, and a wake word |
| `SystemPrompt` | built in | Instructions given to the model |
| `GreetingPrompt` | built in | Line spoken at spawn. `{name}` becomes the player name |
| `PlayerName` | Photon nickname | Your own function for the name used in the greeting |
| `Notification` | built in HUD | Your own notification handler |
| `MinConfidence` | `0.2` | Ignore speech below this confidence |
| `MaxToolSteps` | `5` | How many tool calls one request may use |
| `VoiceInput` | `true` | Set false to skip the microphone entirely |
| `HostControl` | `false` | Allow the agent to launch programs |
| `MenuProvider` | auto | Supply your own list of menu entries |
| `MenuAllow` | allow all | Block specific menu toggles |
| `ToolFilter` | allow all | Block specific tool calls |

### Adding a tool

A tool is a function the model can call by voice. You give it a name, one sentence
describing what it does, the arguments it takes, and the code to run.

```csharp
NullAgent.AddTool(
    "set_speed",
    "Set the player's movement speed multiplier.",
    new[] { "multiplier" },
    args =>
    {
        if (!float.TryParse(args["multiplier"], out float value))
            return "that isn't a number";
        value = Mathf.Clamp(value, 0.1f, 20f);
        Vars.speed = value;
        return "speed set to " + value;
    });
```

The model reads your description to decide when the tool is relevant. Whatever string you
return is fed back to it, so keep the wording short and plain.

If the action did not work, return an explanation instead of a success message. The model
will tell the player it failed rather than pretending it worked. This is the main reason
tools feel honest.

To remove one of the built in tools:

```csharp
NullAgent.RemoveTool("play_song");
```

### Using your own menu entries

If your menu is not a list of objects with a name and an on/off flag, give the library a
provider. This is the most reliable option and it works with any structure.

```csharp
NullAgent.UseMenu(
    () => ModHandler.AllMods.Select(m => new MenuEntry
    {
        Name = m.name,
        Known = true,
        State = () => m.modEnabled,
        Toggle = () =>
        {
            m.modEnabled = !m.modEnabled;
            (m.modEnabled ? m.enabledMethod : m.disabledMethod)?.Invoke();
        }
    }),
    null);
```

`Name` is what the player says out loud. `Known` means you can report the current state.
`State` is optional but makes replies like "already on" work. `Toggle` has to actually
perform the change.

---

## How it finds your menu

You do not have to tell it about your menu. It looks in three places, and the first one
that finds something wins.

**1. Your provider.** If you called `UseMenu`, that list is used and nothing else is checked.

**2. Your code, read directly.** It scans the loaded assemblies for static collections
whose entries have a name of some kind and either a true/false field or an `Enable` and
`Disable` method. Because it reads the real objects, the on/off state it reports is
accurate.

**3. Unity's UI.** If neither of the above finds anything, it looks through every `Button`
and `Toggle` currently in the scene, reads the text label inside each one, and clicks it.

Names are matched loosely. Both sides get lowercased and stripped of spaces and punctuation,
an exact match wins first, then the longest partial match of at least four characters.
This is why "turn off no leaves" finds a mod called `NoLeaves`.

The scan skips the .NET and Unity assemblies so it stays fast, and results are cached for a
few seconds rather than recalculated on every request.

---

## Safety

The model is semi uncensored and runs locally, so the limits are enforced in code rather than by  
asking the model nicely.

**Links.** Only `http` and `https` can be opened. `file:`, `javascript:`, `data:`,
`ms-msdt:` and shell protocols are rejected before anything launches.

**Arguments.** NUL bytes and very long values are refused.

**Launching programs.** This is off by default and stays off unless you set
`HostControl = true`. Even then it only starts executables on a fixed list (browsers,
Spotify, Discord, Steam, Notepad, VLC and similar), refuses anything containing shell
characters, and requires a full path to sit inside a standard install folder. `cmd`,
`powershell`, `wscript`, `reg`, `schtasks` and `curl` are always blocked.

**Your own checks.** `MenuAllow` runs before every toggle and `ToolFilter` runs before every
tool call. Return false or a message to block it.

**No shell.** Programs are started directly by their path, never through a command
interpreter, so a crafted request cannot chain commands.

**No leftovers.** The model server, the speech server, and the microphone process are all
attached to a Windows job object. Closing the game closes all of them.

---

## Building from source

```bash
dotnet build "AI LIB\NullAgent.csproj" -c Release
```

The project expects Gorilla Tag in its default Steam location. For a different game, point
it at that install:

```bash
dotnet build "AI LIB\NullAgent.csproj" -c Release -p:GtRoot="C:\Path\To\Game"
```

### What ends up in the DLL

Small files are embedded and unpacked on first run:

- `NullListen.exe`, the microphone reader
- `NullPlay.exe`, which focuses the browser tab and sends media keys
- `boot.mp3` and `listening.mp3`

These add up to about 190 KB.

The large files are downloaded on first run into `BepInEx/NullAgentLib/`:

- `llama-server.exe` and its DLLs, about 31 MB
- `Null.gguf`, the language model, about 1.9 GB
- `whisper-server.exe` and the speech model, about 181 MB

The total comes to roughly 2.1 GB. They stay on disk instead of inside the DLL for two
reasons: an assembly cannot hold that much data, and keeping them separate means one copy
can be shared by several mods.

---

## Credits

Written by Toast.

A few seconds after the spawn greeting, the agent posts one more line:
`AI LIB made with love by toast`. This is informational only. Please leave it.

When the library is referenced from another project it stays inactive until you call
`NullAgent.Attach(this)`, so you control when it starts and when it stops. It only starts
by itself when the assembly is named `NullAgent`, which is the player drop-in case.

If you want to show the credit somewhere in your own UI:

```csharp
NullAgent.Author     // "Toast"
NullAgent.Notice     // "AI LIB made with love by toast"
```

### Using this in your project

You can use this in open source or closed source projects, free of charge. Two things are
required:

1. Credit **Toast** and link this repository, somewhere visible.
2. If you rename it, the credit still has to read **by Toast**. `SuperAgent by Toast` is
   fine. `SuperAgent by You` is not.

Copy this into your README:

```markdown
Includes [NullAgent](https://github.com/toasta-xx/AI-LIB) by Toast.
```

Selling it, or bundling it into a paid product, needs written permission. Forks are fine.

See [LICENSE](LICENSE) for the full terms and [TRADEMARK.md](TRADEMARK.md) for how the
attribution applies.
