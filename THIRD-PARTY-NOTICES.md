# Third-party notices

BG Recorder is MIT-licensed (see [LICENSE](LICENSE)). The distributed application includes the
following third-party components. Versions are the pinned package versions at the time of packaging;
verdicts and provenance were recorded in [`spikes/SpikeC.MmrRoute/LICENSING.md`](spikes/SpikeC.MmrRoute/LICENSING.md).

| Component | Version | License | Project |
|---|---|---|---|
| ScreenRecorderLib | 6.6.0 | MIT | <https://github.com/sskodje/ScreenRecorderLib> |
| NAudio | 2.2.1 | MIT | <https://github.com/naudio/NAudio> |
| Microsoft.Data.Sqlite | 10.0.9 | MIT | <https://github.com/dotnet/efcore> |
| SQLitePCLRaw (bundle_e_sqlite3) | 3.0.3 | Apache-2.0 (SQLite engine: public domain) | <https://github.com/ericsink/SQLitePCL.raw> |
| Dapper | 2.1.66 | Apache-2.0 | <https://github.com/DapperLib/Dapper> |
| Serilog | 4.2.0 | Apache-2.0 | <https://github.com/serilog/serilog> |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 | <https://github.com/serilog/serilog-sinks-file> |
| Velopack | 1.2.0 | MIT | <https://github.com/velopack/velopack> |
| H.NotifyIcon.Wpf | 2.3.0 | MIT | <https://github.com/HavenDV/H.NotifyIcon> |
| Microsoft.Web.WebView2 | 1.0.4078.44 | Microsoft redistribution license (BSD-style) | <https://www.nuget.org/packages/Microsoft.Web.WebView2> |
| Preact | 10.29.7 | MIT | <https://github.com/preactjs/preact> |

The WebView2 **runtime** is not bundled: it is the Evergreen runtime installed and updated by
Microsoft, used here under its own end-user terms. The SDK's redistribution conditions are met by
preserving Microsoft's notice below and not implying endorsement.

---

## MIT License

The following applies to **ScreenRecorderLib** (© sskodje and contributors), **NAudio**
(© Mark Heath and contributors), **Microsoft.Data.Sqlite** (© .NET Foundation and contributors),
**Velopack** (© Velopack contributors), **H.NotifyIcon.Wpf** (© Havendv and contributors), and
**Preact** (© Jason Miller and contributors):

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Apache License 2.0

The following applies to **Dapper** (© Sam Saffron, Marc Gravell, and contributors), **Serilog**
and **Serilog.Sinks.File** (© Serilog contributors), and **SQLitePCLRaw** (© Eric Sink and
contributors):

Licensed under the Apache License, Version 2.0 (the "License"); you may not use these components
except in compliance with the License. You may obtain a copy of the License at
<http://www.apache.org/licenses/LICENSE-2.0>. Unless required by applicable law or agreed to in
writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific
language governing permissions and limitations under the License.

## Microsoft.Web.WebView2

© Microsoft Corporation. All rights reserved. Redistribution and use in source and binary forms are
permitted under the conditions of Microsoft's WebView2 SDK license
(<https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4078.44/License>): redistributions must
reproduce Microsoft's copyright notice and conditions, and neither Microsoft's name nor those of
contributors may be used to endorse or promote products derived from this software without
permission. This software is provided by Microsoft "as is", without express or implied warranties.
