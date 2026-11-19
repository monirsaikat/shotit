namespace ShotIt.Rendering;

/// <summary>An emoji and the words it can be found by.</summary>
public sealed record EmojiEntry(string Emoji, string Keywords);

/// <summary>A hand-picked set of the emoji most useful on screenshots, grouped and searchable.</summary>
public static class EmojiCatalog
{
    // Each line: emoji, a space, then search words. Kept in source so the picker needs no data file.
    private static readonly (string Name, string Icon, string Data)[] Raw =
    [
        ("Smileys", "😀", """
            😀 grinning smile happy
            😃 smiley happy joy
            😄 smile laugh happy
            😁 grin beaming teeth
            😆 laughing haha
            😅 sweat smile relief phew
            🤣 rofl rolling laughing
            😂 joy tears laughing lol
            🙂 slight smile ok
            🙃 upside down silly sarcasm
            😉 wink
            😊 blush happy shy
            😇 innocent angel halo
            🥰 love hearts adore
            😍 heart eyes love
            🤩 star struck wow amazing
            😘 kiss love
            😋 yum tasty delicious
            😛 tongue playful
            😜 wink tongue crazy
            🤪 zany crazy goofy
            🤑 money rich dollar
            🤗 hug hugging
            🤭 oops giggle hand over mouth
            🤫 shush quiet secret
            🤔 thinking hmm wonder
            🤐 zipper mouth secret
            🤨 raised eyebrow suspicious
            😐 neutral meh
            😑 expressionless blank
            😶 no mouth speechless
            😏 smirk smug
            😒 unamused annoyed
            🙄 eye roll whatever
            😬 grimace awkward yikes
            😌 relieved calm
            😔 pensive sad
            😪 sleepy tired
            😴 sleeping zzz
            😷 mask sick
            🤒 thermometer ill fever
            🤢 nauseated sick gross
            🤮 vomit sick
            🥵 hot sweating
            🥶 cold freezing
            🤯 mind blown exploding head shocked
            🥳 party celebrate birthday
            😎 cool sunglasses
            🤓 nerd geek glasses
            🧐 monocle inspect curious
            😕 confused
            😟 worried
            🙁 frown sad
            😮 open mouth surprised
            😲 astonished shocked
            😳 flushed embarrassed
            🥺 pleading puppy eyes please
            😢 cry sad tear
            😭 sob crying loud
            😱 scream fear horror
            😖 confounded
            😣 persevere struggle
            😞 disappointed
            😓 downcast sweat
            😩 weary tired
            😫 tired exhausted
            😤 triumph huff angry
            😡 angry rage mad
            😠 angry mad
            🤬 cursing swearing angry
            😈 devil smiling evil
            💀 skull dead dying
            💩 poop
            🤡 clown
            👻 ghost boo
            👽 alien
            🤖 robot bot
            """),
        ("People", "👍", """
            👍 thumbs up like yes good approve ok
            👎 thumbs down dislike no bad
            👌 ok perfect fine
            ✌️ peace victory
            🤞 fingers crossed luck hope
            🤟 love you gesture
            🤘 rock on horns
            🤙 call me shaka
            👈 point left
            👉 point right
            👆 point up
            👇 point down
            ☝️ index up one
            👋 wave hello bye
            🤚 raised back of hand
            ✋ raised hand stop high five
            🖐️ hand fingers splayed
            🖖 vulcan salute
            👏 clap applause bravo
            🙌 raising hands celebrate hooray
            👐 open hands
            🤲 palms up
            🤝 handshake deal agreement
            🙏 pray please thanks folded hands
            ✍️ writing
            💪 muscle strong flex
            🧠 brain smart think
            👀 eyes look see watch
            👁️ eye look
            👂 ear listen
            👃 nose smell
            👄 mouth lips
            🫵 you point at viewer
            🧑‍💻 technologist developer coder programmer
            👨‍💻 man developer coder
            👩‍💻 woman developer coder
            🧑‍🏫 teacher
            🧑‍🎨 artist designer
            🧑‍🔧 mechanic fix repair
            🙋 raising hand question me
            🤷 shrug dunno whatever
            🤦 facepalm
            🙅 no gesture not allowed
            🙆 ok gesture
            💁 tipping hand info
            🙇 bow sorry
            🏃 running hurry
            🚶 walking
            """),
        ("Symbols", "✅", """
            ✅ check mark done yes correct complete
            ☑️ ballot box check
            ✔️ check mark tick
            ❌ cross wrong no error delete
            ❎ cross mark button
            ⭕ circle hollow red
            🚫 prohibited forbidden no ban
            ⛔ no entry stop
            ⚠️ warning caution alert
            ❗ exclamation important
            ❕ white exclamation
            ❓ question help
            ❔ white question
            ‼️ double exclamation
            ⁉️ exclamation question interrobang
            💯 hundred perfect score
            🔴 red circle dot
            🟠 orange circle
            🟡 yellow circle
            🟢 green circle dot
            🔵 blue circle
            🟣 purple circle
            ⚫ black circle
            ⚪ white circle
            🟥 red square
            🟩 green square
            🟦 blue square
            🟨 yellow square
            ⬆️ up arrow
            ⬇️ down arrow
            ⬅️ left arrow
            ➡️ right arrow
            ↗️ up right arrow
            ↘️ down right arrow
            ↙️ down left arrow
            ↖️ up left arrow
            ↩️ return back
            🔄 refresh reload counterclockwise
            🔃 clockwise repeat
            ➕ plus add
            ➖ minus remove
            ✖️ multiply times
            ➗ divide
            🟰 equals
            ℹ️ information info
            🆕 new
            🆗 ok button
            🆙 up button
            🆒 cool button
            🆓 free
            🔝 top
            🔜 soon
            🔒 locked lock secure private
            🔓 unlocked open
            🔑 key password
            🔔 bell notification
            🔕 muted bell
            📌 pin pushpin
            📍 location pin map
            🎯 target goal bullseye
            💡 idea light bulb tip
            ⭐ star favourite
            🌟 glowing star shine
            ✨ sparkles new shiny magic
            🔥 fire hot lit
            ⚡ lightning fast zap
            💥 boom collision
            💢 anger symbol
            💬 speech bubble comment chat
            💭 thought bubble
            🗯️ angry speech
            ❤️ red heart love
            🧡 orange heart
            💛 yellow heart
            💚 green heart
            💙 blue heart
            💜 purple heart
            🖤 black heart
            🤍 white heart
            💔 broken heart
            ♻️ recycle
            ⏳ hourglass waiting loading
            ⌛ hourglass done time
            ⏰ alarm clock
            ⏱️ stopwatch timer
            """),
        ("Objects", "💻", """
            💻 laptop computer
            🖥️ desktop computer monitor
            ⌨️ keyboard
            🖱️ mouse computer
            🖨️ printer
            📱 phone mobile
            ☎️ telephone
            📷 camera photo
            📸 camera flash screenshot
            🎥 movie camera video
            📹 video camera record
            🎬 clapper action film
            🎙️ microphone podcast
            🎧 headphones music
            🔊 speaker loud sound
            🔇 muted speaker
            📢 loudspeaker announcement
            📣 megaphone
            🔍 magnifying glass search zoom find
            🔎 magnifying glass right search
            🔧 wrench tool fix settings
            🔨 hammer build
            🛠️ tools hammer wrench
            ⚙️ gear settings cog
            🧰 toolbox
            🔗 link chain url
            📎 paperclip attachment
            ✂️ scissors cut
            📝 memo note write edit
            ✏️ pencil edit
            🖊️ pen
            🖌️ paintbrush art
            🖍️ crayon
            📄 page document
            📃 page curl
            📑 bookmark tabs
            📊 bar chart stats
            📈 chart up growth increase
            📉 chart down decrease
            📋 clipboard list
            📁 folder
            📂 open folder
            🗂️ index dividers
            🗑️ trash bin delete wastebasket
            📦 package box shipping
            📧 email mail
            ✉️ envelope letter
            📨 incoming envelope
            📤 outbox upload send
            📥 inbox download receive
            📅 calendar date
            📆 tear-off calendar
            🗓️ spiral calendar
            💾 floppy save
            💿 disc cd
            🔋 battery
            🔌 plug power
            💰 money bag
            💵 dollar banknote
            💳 credit card payment
            🧾 receipt invoice
            🛒 shopping cart
            🎁 gift present
            🏆 trophy winner award
            🥇 gold medal first
            🎉 party popper celebrate tada
            🎊 confetti ball
            🎈 balloon
            🚀 rocket launch ship fast
            ✈️ airplane travel
            🚗 car
            🏠 house home
            🏢 office building
            🌍 globe earth world
            🗺️ map
            🧪 test tube experiment
            🧩 puzzle piece
            🐛 bug insect issue
            🐞 lady beetle bug
            🛡️ shield security
            ☕ coffee tea hot
            🍕 pizza
            🍔 burger
            🎂 birthday cake
            """),
        ("Nature", "🌿", """
            ☀️ sun sunny
            🌤️ sun behind cloud
            ☁️ cloud
            🌧️ rain
            ⛈️ storm thunder
            ❄️ snowflake cold winter
            🌈 rainbow
            🌙 moon night
            💧 droplet water
            🌊 wave water ocean
            🌱 seedling grow plant
            🌿 herb leaf
            🍀 four leaf clover luck
            🌳 tree
            🌵 cactus
            🌸 cherry blossom flower
            🌹 rose flower
            🌻 sunflower
            🍁 maple leaf autumn
            🐶 dog puppy
            🐱 cat kitten
            🐭 mouse
            🐰 rabbit bunny
            🦊 fox
            🐻 bear
            🐼 panda
            🐯 tiger
            🦁 lion
            🐮 cow
            🐷 pig
            🐸 frog
            🐵 monkey
            🙈 see no evil monkey
            🙉 hear no evil monkey
            🙊 speak no evil monkey
            🐔 chicken
            🐧 penguin
            🦉 owl
            🦄 unicorn
            🐝 bee honeybee
            🦋 butterfly
            🐢 turtle slow
            🐍 snake python
            🐙 octopus
            🐟 fish
            🐬 dolphin
            🐳 whale
            """),
        ("Flags", "🏁", """
            🏁 chequered flag finish race
            🚩 red flag problem
            🏳️ white flag surrender
            🏴 black flag
            🇧🇩 bangladesh
            🇮🇳 india
            🇵🇰 pakistan
            🇺🇸 united states usa america
            🇬🇧 united kingdom uk britain
            🇨🇦 canada
            🇦🇺 australia
            🇩🇪 germany
            🇫🇷 france
            🇪🇸 spain
            🇮🇹 italy
            🇯🇵 japan
            🇨🇳 china
            🇰🇷 south korea
            🇸🇦 saudi arabia
            🇦🇪 united arab emirates uae
            🇹🇷 turkey
            🇧🇷 brazil
            🇲🇾 malaysia
            🇸🇬 singapore
            🇳🇵 nepal
            🇱🇰 sri lanka
            🇪🇺 european union
            🇺🇳 united nations
            """),
    ];

    private static readonly Lazy<IReadOnlyList<(string Name, string Icon, IReadOnlyList<EmojiEntry> Items)>> Parsed = new(() =>
        Raw.Select(c => (c.Name, c.Icon, (IReadOnlyList<EmojiEntry>)Parse(c.Data))).ToList());

    public static IReadOnlyList<(string Name, string Icon, IReadOnlyList<EmojiEntry> Items)> Categories => Parsed.Value;

    public static IEnumerable<EmojiEntry> All => Categories.SelectMany(c => c.Items);

    /// <summary>Emoji whose words start with every word typed (so "thu up" finds 👍), best matches first.</summary>
    public static IReadOnlyList<EmojiEntry> Search(string? query)
    {
        var terms = (query ?? string.Empty).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        return All
            .Select(e => (Entry: e, Words: e.Keywords.Split(' ')))
            .Where(x => terms.All(t => x.Words.Any(w => w.StartsWith(t, StringComparison.Ordinal))))
            .OrderBy(x => x.Words[0].StartsWith(terms[0], StringComparison.Ordinal) ? 0 : 1)
            .Select(x => x.Entry)
            .Distinct()
            .ToList();
    }

    private static List<EmojiEntry> Parse(string data)
    {
        var list = new List<EmojiEntry>();
        foreach (string raw in data.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0) continue;
            list.Add(new EmojiEntry(line[..space], line[(space + 1)..].Trim().ToLowerInvariant()));
        }

        return list;
    }
}
