# Prompts that use the graph

With the `codegraph` MCP server connected, Claude can answer structural questions from a small
graph query instead of reading whole files. You don't need to name the tools. Ask the question
and Claude picks them, but saying "use codegraph" helps in a new session.

Each example below shows the prompt, the tools it usually triggers, and why the graph helps.

---

### 1. Before changing a shared method

> I want to change `WorkoutService.SaveAsync` to take a `CancellationToken`. What breaks, and which tests cover it?

**Tools:** `find_symbol` → `impact_of` → `snippet` on the few callers that matter.

`impact_of` walks callers, overrides and implementations transitively and lists the test
methods that reach the method, all in one answer. Without the graph, Claude would grep for
`SaveAsync`, which also matches unrelated methods with the same name and misses calls through
an interface. The graph uses compiler-resolved symbols, so it gets both right.

---

### 2. Who implements an interface

> List every class that implements `ISessionStep`, including subclasses of those classes.

**Tools:** `find_symbol` → `implementors_of`.

Text search finds `: ISessionStep` but misses `class TimedStep : BaseStep` where `BaseStep`
implements the interface. `implementors_of` follows both `Implements` and `Inherits` edges, so
those indirect implementors are included.

---

### 3. Where a type is used

> Where is `WorkoutPlan` used? I'm thinking of making it a record.

**Tools:** `find_symbol` → `references_to`, then `impact_of` on the type if the list is long.

`references_to` lists every member that mentions the type: parameters, return types, fields,
locals, `new WorkoutPlan()`, casts, generic arguments such as `List<WorkoutPlan>`, attributes
and record parameters. Inheritance is handled separately, by `implementors_of` and `inheritors_of`.

---

### 4. How does A reach B

> How does `SessionsController.Complete` end up calling `EmailSender.Send`?

**Tools:** `find_symbol` (twice) → `path_between`.

This returns the shortest call chains, one hop per line. It answers "why is this request
sending email?" without Claude opening every file along the way.

---

### 5. Reviewing a diff for test coverage

> For each method changed in this branch, tell me which tests reach it. Flag any that no test reaches.

**Tools:** `find_symbol` per changed method → `covered_by` (transitive) or `tests_for` (direct), plus `uncovered` scoped to the changed files.

This fits `/code-review` or a pre-merge check well. "Reached by a test" is static reachability
through the call graph, not line coverage, so treat a hit as "some test calls this", not
"this is well tested".

---

### 6. Finding dead code safely

> Find private and internal methods that look unused in `GymX.Core`, and explain why each one is a candidate.

**Tools:** `dead_code_report` → `explain_reachability` on anything surprising.

The report already accounts for interface dispatch (a class method called through its interface
counts as used) and framework entrypoints such as Azure Functions triggers. Those two cases are
what make "zero references" searches give false positives.

---

### 7. Getting oriented in unfamiliar code

> Give me an outline of `RouteExecutor`: its members, what it calls, and who calls it. Don't read the whole file.

**Tools:** `find_symbol` → `outline` → `callers_of` / `callees_of` → `snippet` on one or two members.

`outline` returns member signatures and line spans with no bodies, so Claude sees the shape of
the class for a few hundred tokens. It then reads only the spans it needs.

---

### 8. Finding code by what it does

> Where's the code that retries a failed upload? I don't know what it's called.

**Tools:** `search_code` → `snippet` on the top hit or two.

`search_code` is a ranked full-text search over every type and member: names split on case
boundaries, doc and leading comments, signatures, string literals and the identifiers in bodies.
"retry failed upload" finds `UploadQueue.RetryFailed` and the `// retried once on 5xx` comment
inside `BlobSender.Send` without a grep, and each hit shows how many of the query's words it
matched. Once a name is known, switch to `find_symbol` and the graph tools.

---

### 9. Is the graph earning its keep?

> How much has codegraph been used in this project, and how much reading did it save?

**Tool:** `usage_stats`.

Calls per tool, latency, answer size, and the size of the source files each answer pointed at,
for this session and all time. Tokens are estimated; "saved" assumes the files would otherwise
have been read in full.

---

## When answers look wrong

> Is the code graph up to date?

**Tool:** `index_status`.

Every answer is already prefixed with `⚠ STALE INDEX` when the working tree has changed since
indexing, and rows from edited files end in `STALE`. Re-run `codegraph-index <solution>` to
refresh, which is incremental. Ask for `index_status` to see the commit the graph was built at
and the full list of changed files.

## Tips

- **Ask structural questions, not textual ones.** "Who calls", "what implements", "what breaks"
  and "which tests" are what the graph is for. "Where's the code that does X" goes to
  `search_code`; to find one exact string, plain grep is still the right tool.
- **Name the symbol as precisely as you can.** `WorkoutService.SaveAsync` resolves in one call,
  while `Save` may return twenty matches.
- **Callers outside the solution are invisible.** The graph only knows the indexed solution.
  Reflection, DI registration by convention and calls from other repos won't show up.
