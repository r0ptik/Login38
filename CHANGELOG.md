# Changelog

*English · [繁體中文](CHANGELOG.zh-TW.md)*

## 1.2.0

The hunt no longer runs on servers that never offered it.

### A client that had to be clicked once per step

Players reported that walking and attacking stopped being continuous: the character took
one step, or one swing, per press of the left mouse button. It happened on servers whose
operator had ticked "開啟自動狩獵" in the encoder, and it happened whether or not the
player had switched hunting on.

The operator's switch was only ever enforced where the settings window writes its copy
back, so it decided which tab was drawn and nothing else. The helper loop read the
character's saved profile straight, so a profile holding the hunt switch on was hunted with
regardless — and a hunt nobody had asked for was pinning the client's hover target and
clearing its walk and attack flags underneath a player who was playing for themselves.

The switch is now read by the loop before it will touch the client at all, and it defaults
to off: a game whose offer was never read is a game the hunt leaves alone.

### The hunt no longer clears flags it did not set

Tearing the chain down wrote the client's walk and attack flags whether or not this hunt
had armed anything, and it ran on every pass — five times a second while the hunt was
switched off. The walk engine returns the moment its flag is clear, which is the same
character taking one step per click. Only what the hunt actually armed is taken back out
now.

### Low-CPU throttling asked the wrong question

It compared the foreground window against a handle. This client keeps more than one
top-level window, so the handle found at install time is often not the one holding the
focus, and the throttle then read a client being played as one sitting in the background.
It asks which process owns the window in front instead, which is the question the rest of
the launcher already asked.

### Packet logging removed

The diagnostic that recorded every packet the client sent is gone, along with its box in
the helper window. It hooked the client's own network path, which is not something to ship
to players for a tool only ever used to work out what one build sends.

## 1.1.0

Automatic hunting, and the reason skills used to miss.

### Hunting

The helper can now fight on its own: pick a target, close to it, keep a skill rotation
going, retaliate when something hits first, and stop when health runs low. Its page in the
helper window appears only when the server list has `internal_bot_enabled` set — the
operator decides whether the build offers it at all.

### Casting was aimed at an address the client does not use

Every skill went out aimed at `0x0097C90C`, which nothing in the client reads. The cast
landed on whatever target the client happened to be holding, or on nothing — in which case
it armed the "choose a target" cursor and waited for a click. That is why skills seemed to
fire at monsters nobody chose, and why they sometimes did not fire at all.

Casting is now assembled from the client's own routines rather than dispatched through its
spell book entry point: the cast delay, the packet, and both cooldown stamps, with none of
the words that decide what the player's next click means. Typing or browsing a bag while
the helper casts no longer arms the cursor, and where the mouse is pointing no longer
steers what the helper casts at. The rate is bounded by the client's own cooldown.

### The client says why a cast was refused, so the helper reads it

The server answers a refused cast with a numbered message and the client writes it into its
own chat window. The helper watches for the nine that mean a cast did not happen — too
heavy, out of mana or health, nothing in line, interrupted, and the rest — and switches to
the weapon until the condition clears. Each reason is held against the thing that would end
it: weight against the load the client shows, mana and health against real recovery, line
of sight against the character having moved. The rest are answered by letting one cast
through every so often, waiting twice as long each time it draws the same answer.

Being overweight used to cost a whole hunt: every cast was refused, silently, several times
a second, and nothing was reading the reason.

### Also

- An "ATS" badge drawn inside the game's own frame while the hunt is running.
- Hidden monsters — burrowed, sunk, invisible, flying — are no longer chosen as targets.
- Skills cast from five tiles, which is as far as every ranged skill a character can learn
  still reaches.

## 1.0.1

Scaled present, overlay lifetime, client-owned toggles and exit latency.

## 1.0.0

First public release.
