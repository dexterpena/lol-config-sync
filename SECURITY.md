# Security

If you find a security problem, such as a way for the updater to install something it shouldn't, or a way for another program to abuse the app's access to the League client, please **don't open a public issue**. Report it privately through GitHub's [private vulnerability reporting](https://github.com/dexterpena/lol-config-manager/security/advisories/new) instead.

How the app limits risk:

- It only talks to the League client on `127.0.0.1`, and it accepts the client's self-signed certificate only for that address.
- Updates come only from this repository's GitHub Releases over HTTPS. Downloads are checked against the SHA-256 digest GitHub publishes for each release file before anything is run.
- It never reads or modifies the game process, and it stores no passwords or tokens.
