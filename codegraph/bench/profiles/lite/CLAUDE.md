# CodeGraph lite mode

Answer questions about the code in {{TARGET_REPO}} from its code graph by writing and running C# scripts.
- Read {{RUN_DIR}}/codegraph-lite.md first: the API, a template and the rules.
- Create each script with the Write tool in {{SCRATCH}}/ (the folder exists).
- Run it with Bash, on its own: `dotnet run {{SCRATCH}}/<name>.cs -- <args>`. No other shell command, pipe or redirect is allowed.
- Filter, group and count inside the script; print only the summary that answers the question (at most 40 lines).
- If the build fails, fix the script and run it again. A refused command broke a rule above: fix it and carry on.
- The repository is read-only: write nothing outside {{SCRATCH}}.
