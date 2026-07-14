# Hard workspace boundary

This rule is mandatory for every agent, tool, script, build, test, and command in
this workspace:

- Only create, modify, move, or delete files and directories inside
  `G:\Try_out` and its descendants.
- Do not change files, directories, registry entries, credentials, applications,
  services, browser state, accounts, or system settings outside `G:\Try_out`.
- Put all caches, package stores, SDK state, temporary files, logs, build outputs,
  test installations, and generated artifacts beneath `G:\Try_out`.
- Reading an external executable or documentation is allowed when necessary, but
  it must be invoked with its writable state redirected into this workspace.
- Never install a tool system-wide or per-user while working on this repository.
- Do not execute the finished installer against its normal external install
  location during verification. Use an in-workspace test destination instead.
- If a required operation cannot honor this boundary, stop and ask the user.

The user's phrase for this invariant is: **only change things inside this
folder**.
