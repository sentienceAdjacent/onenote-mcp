# Third-party notices

This project's own code is licensed as described in [LICENSE.md](LICENSE.md). The items below are not covered by that license. Each keeps its own terms.

## Vendored in this repository

### OneNote 2013 XML schema

- **File:** `src/OneNoteMcp.Xml/Schema/OneNoteApplication_2013.xsd`
- **Source:** published by Microsoft in the 2013 blog post "OneNote 2013 COM API XML Schema (OneNote 2013 XSD)". The file here is a byte-for-byte copy of a Wayback Machine snapshot. Provenance and the SHA-256 are in `src/OneNoteMcp.Xml/Schema/PROVENANCE.md`.
- **Terms:** Microsoft stated no redistribution terms. The schema is Microsoft's and is included only so that the server can validate OneNote's own XML. The project's license doesn't apply to it.

## Included in the release executable

The released `OneNoteMcp.exe` is a self-contained .NET application. It bundles the .NET runtime and these packages:

| Component | License |
|---|---|
| .NET runtime and libraries (Microsoft) | MIT |
| `Microsoft.Extensions.Hosting` and its dependencies (Microsoft) | MIT |
| `System.Security.Cryptography.Xml` (Microsoft) | MIT |
| `System.Security.Cryptography.ProtectedData` (Microsoft) | MIT |
| `ModelContextProtocol` (the MCP C# SDK) | Apache-2.0 |
| `Serilog.Extensions.Logging` | Apache-2.0 |
| `Serilog.Sinks.File` | Apache-2.0 |

Each package's license text ships in its NuGet package and on its project site. Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0. MIT: https://opensource.org/license/mit.

## Prior art

[OneMore](https://github.com/stevencohn/OneMore) by Steven M Cohn (MPL-2.0) informed much of this project's understanding of OneNote's COM API. No OneMore source code is included.
