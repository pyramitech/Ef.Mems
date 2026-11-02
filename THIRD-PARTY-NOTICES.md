# Third-party notices

Ef.Mems is licensed under the Apache License 2.0 (see `LICENSE`). The
redistributed package additionally contains the following third-party software.

## RazorBlade

The `Ef.Mems.Generator` analyzer embeds the RazorBlade runtime library as
source (`RazorBladeEmbeddedLibrary`), so the compiled analyzer DLL contains
RazorBlade code.

- Project: https://github.com/ltrzesniewski/RazorBlade
- License: MIT

```
MIT License

Copyright (c) Lucas Trzesniewski

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

## Runtime dependencies (not redistributed)

The package declares NuGet dependencies on Entity Framework Core
(MIT, Microsoft) and ProtoPromise (MIT, Tim Cassell); these are restored by the
consuming project and are not contained in this package.
