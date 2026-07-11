# SvnFlux

SvnFlux is a pure managed .NET 10 implementation of selected Apache Subversion protocols, repository abstractions, and svndiff formats.

Packages:

- SvnFlux.Core - transport-independent repository contracts and domain model;
- SvnFlux.Svndiff - streaming svndiff 0/1 encoder and decoder;
- SvnFlux.RaSvn - native svn protocol implementation;
- SvnFlux.Repository.FileSystem - directly browsable mutable filesystem repository backend.

The production libraries are implemented in managed C# and do not depend on native Subversion libraries. The official svn command-line client is used only by integration tests.

## Development

    dotnet restore SvnFlux.slnx
    dotnet test SvnFlux.slnx
    dotnet run --project SvnFlux.Playground -- --help

SvnFlux is licensed under the MIT License.
