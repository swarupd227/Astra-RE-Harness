---
id: docs-language-guidance
version: v1.0
owner: Nous · Documentation generation
notes: |
  One block per source language, appended as the LAST system block of every
  documentation writer call (DocPromptAssets.LanguageBlock), so the shared
  prompts stay language-neutral and each language's conventions are named once.
  A section heading lists the language ids it serves, comma-separated; `default`
  answers any language without its own block. Keep each block short: the
  points a transition engineer must look for in that language, nothing else.
---

## fortran-f77, fortran
Source language: Fortran (FORTRAN 77 and later).
- Shared state lives in COMMON blocks; name the block and its fields when a routine reads or writes them.
- Errors are usually reported through XERBLA or an INFO/IER argument; treat XERBLA as the error boundary.
- Watch numerical traps: zero or negative strides and counts, near-zero pivots, overflow-prone dimensions, precision variants (S/D/C/Z).
- Arrays are passed by reference with leading dimensions; say which arguments are updated in place.
- File units (unit numbers) are the external interfaces; implicit typing can hide type assumptions.

## cobol
Source language: COBOL.
- Units of work are paragraphs and sections inside a program; PERFORM is the call, GO TO is a jump.
- Data lives in WORKING-STORAGE, LINKAGE SECTION and copybooks; name the record and field (PIC clauses carry the format).
- Files (SELECT/FD, VSAM, sequential), DB2 (EXEC SQL) and CICS commands are the external interfaces; JCL DD names are their logical names.
- Error handling is via FILE STATUS, SQLCODE and return codes; name the code a caller must check.
- Watch packed-decimal and implied-decimal arithmetic, rounding and truncation.

## php
Source language: PHP.
- Request input arrives through superglobals ($_GET, $_POST, $_REQUEST, $_COOKIE, $_FILES, $_SERVER); name each parameter a routine reads and what it means.
- Sessions ($_SESSION) and cookies carry state between requests: treat them as shared state and name the keys.
- SQL is often written inline (PDO, mysqli, a DB wrapper); name the tables read and written, and say whether parameters are bound or concatenated.
- include/require pull in other files and can define globals; global variables and static class properties are shared state.
- Pages often mix HTML output with logic; output (echo, templates, header(), redirects, HTTP status) is part of the behaviour.
- Loose typing matters: isset/empty, string↔number coercion, null vs "" vs 0, arrays used as both lists and maps.
- Errors surface as exceptions, false/null return values, error messages stored for the next page, or an HTTP error/redirect.

## java
Source language: Java.
- Units are methods of classes; name the class and whether the method is static, and the fields it reads or mutates.
- External interfaces: JDBC/JPA (tables), HTTP clients and controllers, JMS/queues, files, configuration properties.
- Errors are exceptions: name checked exceptions thrown and which ones are caught and swallowed.
- Watch null handling, Optional, collections mutated in place, and transaction boundaries.

## csharp, dotnet, vbnet
Source language: C# / .NET.
- Units are methods and properties; name the class, async/await behaviour and any static or injected state.
- External interfaces: EF/ADO.NET (tables), HttpClient, queues, files, app settings and connection strings.
- Errors are exceptions; name what is thrown, caught and logged. Watch null-reference paths and nullable value types.
- LINQ queries can defer execution; say when a query actually runs.

## vb6, vba
Source language: Visual Basic 6 / VBA.
- Units are Sub, Function and Property procedures in modules, classes and forms; event handlers (Click, Change, Workbook_Open) are entry points.
- In Office documents, worksheet cells and named ranges are inputs and outputs: name the sheet and cell/range a routine reads or writes.
- Forms and controls (TextBox, ComboBox, buttons) carry user input; name the control.
- Module-level and Public variables are shared state; On Error GoTo/Resume Next decide what happens on failure.
- External interfaces: ADO/DAO databases, files, COM objects, other workbooks, MsgBox prompts.
- Watch Variant coercion, 1-based vs 0-based arrays, and implicit default properties.

## delphi, pascal
Source language: Delphi / Object Pascal.
- Units are procedures, functions, methods, constructors and destructors in units; name the class and unit.
- Shared state: unit-level variables, class fields, global singletons; components on forms (TDataSet, TQuery) carry data.
- Errors are exceptions (try/except/finally); ownership and Free decide object lifetime.
- External interfaces: datasets and SQL components, files, COM, sockets, registry/INI settings.

## cpp, c
Source language: C / C++.
- Units are functions and methods; name the class/namespace and whether arguments are passed by pointer or reference and modified.
- Ownership and lifetime matter: who allocates and frees, RAII, smart pointers; undefined behaviour is a trap worth naming.
- Shared state: globals, statics, singletons; errors via return codes, errno or exceptions.
- External interfaces: files, sockets, OS calls, database client libraries.

## unibasic, pick, openedge, abl
Source language: a MultiValue / 4GL language (UniBasic, Pick BASIC, OpenEdge ABL).
- Units are subroutines, programs and internal procedures; CALL/RUN is the call.
- Data lives in files/tables with multi-valued fields (attributes, values, sub-values); name the file and attribute positions.
- COMMON / named common and shared variables are shared state; record locks (READU/WRITE, FIND EXCLUSIVE-LOCK) matter.
- Errors via ELSE/ON ERROR clauses and status codes; name what the caller must check.

## default
Source language: as named in the input.
- Describe units with the source language's own terms (function, method, procedure, paragraph, macro).
- Name shared or global state, the external interfaces it touches (database, files, network, UI), and how errors are reported.
