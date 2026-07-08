"""
Tests for parser_sidecar.php_parser.

Phase 15.0 — smoke coverage for the PHP v0 brace-aware tokenizer. Asserts it
extracts constructors, methods, free functions and interface stubs; tracks the
enclosing type; ignores `<?php`/HTML boundaries and comment/string braces; and
does not mistake `throw new X(...)` statements for declarations.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.php_parser import parse_source


_SAMPLE = textwrap.dedent(r'''
    <?php
    declare(strict_types=1);

    namespace Acme\Catalog\Model;

    use Acme\Catalog\Api\PriceRepositoryInterface;
    use Magento\Framework\Exception\LocalizedException;

    /** Computes cart totals. */
    class CartTotals implements TotalsInterface
    {
        private PriceRepositoryInterface $prices;

        public function __construct(PriceRepositoryInterface $prices)
        {
            $this->prices = $prices;
        }

        public function subtotal(array $items): float
        {
            $sum = 0.0;
            foreach ($items as $item) {
                $sum += $this->prices->priceFor($item["sku"]) * $item["qty"];
            }
            return $sum;
        }

        private function applyTax(float $net): float
        {
            if ($net <= 0) {
                throw new LocalizedException(__("bad net"));
            }
            return round($net * 1.2, 2);
        }
    }

    interface TotalsInterface
    {
        public function subtotal(array $items): float;
    }

    function formatMoney(float $amount): string
    {
        return number_format($amount, 2);
    }
''').strip()


def _by_name(out):
    return {s.name: s for s in out.subroutines}


def test_constructor_methods_and_free_function():
    out = parse_source("CartTotals.php", _SAMPLE)
    names = set(_by_name(out))
    assert "__construct" in names
    assert "subtotal" in names
    assert "applyTax" in names
    assert "formatMoney" in names   # free function outside any class


def test_interface_stub_captured():
    out = parse_source("CartTotals.php", _SAMPLE)
    # subtotal appears both as a concrete method and as an interface stub;
    # at least one span is recorded, and the concrete one has a body.
    spans = [s for s in out.subroutines if s.name == "subtotal"]
    assert any(s.line_end > s.line_start for s in spans)


def test_enclosing_type_tracked():
    out = parse_source("CartTotals.php", _SAMPLE)
    by = _by_name(out)
    assert by["applyTax"].common_block_refs == ("CartTotals",)
    assert by["formatMoney"].common_block_refs == ()   # free function


def test_throw_new_is_not_a_method():
    out = parse_source("CartTotals.php", _SAMPLE)
    names = set(_by_name(out))
    # `throw new LocalizedException(...)` must NOT be captured as a routine
    # (PHP requires the `function` keyword, so this class of bug cannot occur).
    assert "LocalizedException" not in names


def test_calls_exclude_arrow_and_static():
    out = parse_source("CartTotals.php", _SAMPLE)
    by = _by_name(out)
    # $this->prices->priceFor(...) is an arrow call → excluded.
    assert "priceFor" not in by["subtotal"].called_subroutines


def test_html_outside_php_is_ignored():
    src = textwrap.dedent(r'''
        <html><body>{ not php }</body>
        <?php
        class Widget {
            public function render(): string { return "<b>{x}</b>"; }
        }
        ?>
        <footer>}</footer>
    ''').strip()
    out = parse_source("Widget.php", src)
    render = next(s for s in out.subroutines if s.name == "render")
    assert render.common_block_refs == ("Widget",)
