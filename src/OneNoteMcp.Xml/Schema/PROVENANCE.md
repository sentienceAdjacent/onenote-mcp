# OneNoteApplication_2013.xsd provenance

The OneNote 2013 COM API XML schema, vendored byte-for-byte (AD-16). Do not edit, reformat or re-save it: a unit test (`XmlModelTests.Xsd_IsTheVendoredWaybackBytes`) pins its SHA-256, and `.gitattributes` marks `*.xsd` `-text` so Git never rewrites its CRLF line endings.

| | |
| --- | --- |
| Published by | Microsoft, in the 2013 blog post "OneNote 2013 COM API XML Schema (OneNote 2013 XSD)", now archived at https://learn.microsoft.com/en-us/archive/blogs/descapa/onenote-2013-com-api-xml-schema-onenote-2013-xsd |
| Original download | `https://msdnshared.blob.core.windows.net/media/MSDNBlogsFS/prod.evol.blogs.msdn.com/CommunityServer.Blogs.Components.WeblogFiles/00/00/00/63/17/0336.OneNoteApplication_2013_xsd.txt`, which returns HTTP 403 AuthorizationFailure (checked 2026-11-03) |
| Vendored from | Wayback Machine snapshot `http://web.archive.org/web/20200727033613/https://msdnshared.blob.core.windows.net/media/MSDNBlogsFS/prod.evol.blogs.msdn.com/CommunityServer.Blogs.Components.WeblogFiles/00/00/00/63/17/0336.OneNoteApplication_2013_xsd.txt`, fetched raw through the `20200727033613id_` form of that URL |
| Retrieved | 2026-11-04 |
| Size | 47,961 bytes, CRLF line endings |
| SHA-256 | `fcddc26ac8fa925a4dac86e0b3cfc451445e2728757d4804ed1e0e1cbfb2a29d` |
| Redistribution terms | None stated by Microsoft |

## Comparison with OneMore's copy

[stevencohn/OneMore](https://github.com/stevencohn/OneMore) (MPL-2.0) carries the same schema at `Reference/0336.OneNoteApplication_2013.xsd`, with no recorded provenance. Its current copy on `main` (fetched 2026-11-04) is 46,672 bytes, SHA-256 `fbb4710bf5f159ee5b2ac7488fdeb4b1e3529f5b6e86157624fdb07118879cc6`. It differs from the Wayback bytes only in line endings (LF instead of CRLF) and two trailing blank lines; the schema content is the same. An earlier OneMore revision recorded in `research-onemore-lessons.md` row 9 was 47,957 bytes (`2f3a01f0…`). We vendor the Wayback bytes because they are the closest available copy of Microsoft's original download.
