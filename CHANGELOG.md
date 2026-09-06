# Changelog

*English · [繁體中文](CHANGELOG.zh-TW.md)*

## 1.2.0

Automatic hunting no longer runs on servers that never offered it.

### One click per step, one click per swing

Walking and attacking stopped being continuous. It happened on any server whose operator
ticked automatic hunting in the encoder, whether or not the player had turned hunting on.

The operator's switch only ever reached the settings window, so it decided which tab was
drawn and nothing else. The helper loop read the character's saved profile instead, and
hunted with whatever it found there. A hunt nobody had asked for was steering the client
while the player was trying to play it.

The loop reads the switch now, and it defaults to off.

### The hunt stops clearing flags it did not set

Tearing the chain down wrote the client's walk and attack flags whether or not the hunt had
started anything, five times a second while it was switched off. The walk engine stops the
moment that flag is clear. It now takes back out only what it put in.

### Hunting knows what a teleport is

Cave mouths and other exits are read out of the client's collision grid, and the hunt will
not route onto one or park beside it. Walking into one used to drop the character on
another floor still carrying a route, an ignore list and a collision window belonging to
the map it had left. Changing floor now starts the hunt over.

Only the launcher's own copy of the grid is marked, so the player can still walk wherever
they like.

### Low-CPU throttling asked the wrong question

It compared the foreground window by handle. The client keeps more than one top-level
window, so the throttle regularly read a client being played as one sitting in the
background. It asks which process owns the window in front now.

### Packet logging removed

The diagnostic that recorded every packet the client sent is gone, with its box in the
helper window. It hooked the client's own network path, which is not something to ship to
players.

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
