# Third-party notices

This distribution includes third-party components. Original project code is supplied to the commissioning user; no additional open-source license is assigned to that original code here.

| Component | Version | Upstream / license |
|---|---|---|
| Vortice.Direct3D11, DXGI, DirectX | 3.8.3 | https://github.com/amerkoleci/Vortice.Windows — MIT |
| Vortice.Mathematics | 2.1.0 | https://github.com/amerkoleci/Vortice.Mathematics — MIT |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta (transitive dependency selected by Vortice) | https://github.com/SharpGenTools/SharpGenTools — MIT |
| .NET runtime and Windows Desktop runtime | 10.0.12 | https://github.com/dotnet/runtime and https://github.com/dotnet/wpf — bundled license files |
| Microsoft.Windows.SDK.NET.Ref / WinRT projections | 10.0.19041.57 | https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.19041.57 — Microsoft package terms; C#/WinRT runtime uses MIT |

NuGet resolution and integrity hashes are in `App/packages.lock.json`. The SharpGen beta suffix is an upstream transitive dependency; it is disclosed here and covered by the local integration test, not claimed to have a separate stable-release designation.

## MIT notices

Vortice.Windows: Copyright (c) Amer Koleci and Contributors.

Vortice.Mathematics: Copyright (c) Amer Koleci and contributors.

SharpGen.Runtime package copyright: (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci.

C#/WinRT: Copyright (c) Microsoft Corporation.

The following MIT license applies to the MIT components identified above:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The Microsoft runtime license texts included with the restored packages are copied into `Release/licenses`. Keep this notice and those files with redistributed binaries.
