"""Astra PHP sidecar — runs one PHP function or method with given inputs in an isolated `php` process and returns
what it observed (return value, echoed output, response code, headers, by-reference outputs, session writes,
exception). The API's CrossRuntimeValidator drives it beside the maven sidecar to check a java-spring package
generated from PHP against the PHP source, vector by vector."""

__version__ = "0.1.2"
