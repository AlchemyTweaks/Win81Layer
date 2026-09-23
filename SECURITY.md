# Security Policy

## Reporting a vulnerability

Please do not open a public issue for a security problem, and do not post details
in a pull request.

Report it privately through GitHub instead:

1. Go to the Security tab of this repository.
2. Choose "Report a vulnerability" to open a private advisory.

Please include:

- What the problem is and how serious you think it is
- Steps to reproduce it
- The affected file or feature if you know it

You will get a reply as soon as possible. Please give a reasonable amount of time
for a fix before you share the details publicly.

## Scope

This project is a desktop shell that runs as a normal user program. Reports that
are most useful include:

- Code that could run with higher privileges than intended
- Handling of the optional Google OAuth credentials
- Anything that could let untrusted input run code or change system settings

Thank you for helping keep the project safe.
