# The story

*Author: Alan Seunarayan*

[Home](README.md) · [Installation](installation.md) · [Usage](usage.md) · [Obsidian](obsidian.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · [Performance](performance.md) · [Architecture](architecture.md)

---

This page is for anyone who wonders where CodeGraph came from, and whether they could build something
like it. The short answer to the second question is yes. I am a developer, not a compiler engineer or a
database person, and the thing that made this possible is that AI coding assistants let one person with
an idea get it out of their head and into a working program before the idea goes cold.

## The itch

I use Claude Code a lot. Watching it work on a C# solution, one thing kept bothering me. I would ask
"what breaks if I change this method?" and it would grep for the name, open a dozen files, read them to
tell the real calls from the look-alikes, then grep again for each caller's callers, then look for the
tests. Every file it opened went into its context and stayed there. It was slow, it cost tokens, and it
could still miss a caller that went through an interface or a base class, because nothing in the text
said so.

The thought that would not go away was: *the compiler already knows all of this*. Roslyn resolves every
call, every override, every interface implementation when it builds the solution. That knowledge was
right there, and the assistant was reconstructing it by hand, badly, on every question.

## The first evening

The first commit is from 19 August 2026. It holds a Roslyn indexer that walks a solution and writes
types, members and call edges into a DuckDB file, and an MCP server that answers "who calls this" from
that file. By the end of the same day there was a graph viewer and `path_between`, and the day after,
dead-code analysis. None of that was planned. Each piece suggested the next.

I want to be honest about how it was built, because it is the point of this page. I described what I
wanted, Claude Code wrote most of the code, and I tested it, argued with it, and pointed it at the next
thing. When the Roslyn API did something I did not understand, I asked. When DuckDB's bulk write was slow,
we measured it together and fixed it. I was still the one deciding what the tool should do and whether
an answer was right, and that turned out to be the job.

## Growing up

Then it sat for two months, which is what happens to most side projects. What brought it back was using
it. The graph went stale the moment I edited a file, and the assistant would reason from an old graph
without knowing. So the next piece was incremental indexing with a staleness check on every answer: if
the working tree has moved on, the answer says so in its first line.

From there the questions kept coming from use, not from a roadmap:

- "Why does this take ten seconds every time?" led to the live daemon, which keeps the compiled solution
  in memory and re-indexes a save in well under a second. Once the compiler was resident, it could also
  answer things a stored graph cannot: compiler errors, and what an edit would break *before* writing it.
- "My architect wants to see this, and she does not use Claude Code" led to the Obsidian export: the same
  graph as linked notes, so a person can browse it with no editor and no tokens.
- "Our front end is Angular" led to a TypeScript extractor, then to a language-neutral record format,
  then to Java, Go, Rust and Python through their SCIP indexers.
- "Does it work on a repo that is still on .NET 8 and C# 7?" led to the version checks, the generated
  corpus in two dialects, and the Docker matrix of older toolchains.

Each of those was a few evenings. A few years ago each would have been a month, and I would have stopped
after the first one.

## What I learned

**The engineering ran ahead of the product.** By November the indexer was precise, fast and well tested,
and nobody but me could install it without a .NET SDK, a checkout and a build. That is the gap this
release closes: prebuilt binaries, an installer, a Claude Code plugin, and this documentation sorted by
who is reading it. The [roadmap](roadmap.md) lists what is still missing, in order.

**Measure or you are guessing.** The number that justifies the whole project is on the front page: about
5,300 tokens for an impact question with the graph, against about 105,000 without it. I did not know
that number until I measured it, and it changed what I worked on next.

**Let the tool tell you when it is wrong.** The stale warning, the compiler-version notes, the
`usage` line: every one of them exists because a silent failure cost me an afternoon once.

**You can do this too.** If you have a crazy idea about your tools, the distance between the idea and a
working program has never been shorter. Build the smallest version in an evening. Use it. Let use tell you
what to build next.

---

← Previous: [Home](README.md) · Next: [What is missing](roadmap.md) →
