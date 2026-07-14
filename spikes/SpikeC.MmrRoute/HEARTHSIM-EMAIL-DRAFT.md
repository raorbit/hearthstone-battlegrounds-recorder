# Draft email to HearthSim — HearthMirror licensing

> **This is a draft for the project owner to review, edit, and send. Claude does not send it.**
> Please read it over, adjust the tone/specifics to your liking, and send it yourself.

## Where to send it

- **Primary:** `contact@hearthsim.net` — the contact email listed on the [HearthSim GitHub organization profile](https://github.com/HearthSim) (checked 2026-07-14). HearthSim now operates as **Untapped.gg**; `hearthsim.net` redirects to `untapped.gg/company`, which has a "Contact Us" channel as a fallback.
- **Alternative:** open a polite question as a GitHub Discussion/Issue on a HearthSim repo (e.g. Hearthstone-Deck-Tracker), since HearthMirror lives in that org. Email is the more appropriate channel for a licensing question.

Suggested subject: **Licensing question: HearthMirror for an MIT-licensed open-source Battlegrounds recorder**

## Draft body

Hello HearthSim team,

I'm building a small, open-source (MIT-licensed) Windows tool that records Hearthstone **Battlegrounds** matches to local video, with a library UI and per-combat markers. It watches the game's Power.log the same read-only way Hearthstone Deck Tracker does, and it stores everything locally — no accounts, no cloud, no automation of gameplay.

I'd love to show a player's Battlegrounds rating (and per-match MMR change) alongside each recording. As you know, the rating isn't in any log — it lives in game memory — and HearthMirror is the well-established, read-only way to get it.

Before writing any code that touches HearthMirror, I wanted to ask about its licensing. I noticed the Hearthstone-Deck-Tracker repository is marked "All Rights Reserved," and I couldn't find HearthMirror published under any separate license or on NuGet (as of July 2026). So my questions are:

1. Is HearthMirror available under any license for third-party use?
2. If not, would you consider publishing it on NuGet (or a standalone repo) under a permissive license so tools like mine can depend on it cleanly?

A few things I want to be clear about, out of respect for your work:

- The MMR feature is **completely optional** in my app. It's built as a pluggable "rating provider" — if it isn't available, the app records and plays back matches exactly the same, just without the rating number. **Recording never depends on it.**
- I'm not looking to copy HearthMirror's source into an MIT project without permission. I'd rather ask first. If a license isn't possible, I'll either implement my own minimal reader for just the one rating field, or ship without MMR.
- Happy to credit HearthSim/Untapped prominently and to follow any conditions you'd want (attribution, version pinning, non-commercial, etc.).

Thanks very much for HearthMirror and for everything you've contributed to the Hearthstone tooling community over the years. Any guidance is appreciated.

Best regards,
[Your name]
[Optional: link to the project's public repo]

## Why this ask is framed the way it is

- **Specific asks** (is there a license / would you publish one) so a busy maintainer can answer yes/no quickly.
- **Graceful degradation stated up front** — signals we're not creating a dependency risk for their users and that a "no" doesn't break anything.
- **No source reuse without permission** — acknowledges the "All Rights Reserved" boundary explicitly, which is the whole reason for the email.
- Keeps the door open to their preferred terms rather than demanding a specific license.
