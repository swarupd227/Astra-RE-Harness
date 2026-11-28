<?php
declare(strict_types=1);

/*
 * Astra equivalence — the database a PHP code unit gets in a seeded run.
 *
 * The driver rewrites `new PDO(` / `new \PDO(` in the declarations it loads to `new \AstraSeededPdo(`, so whatever DSN
 * and credentials the application builds (mysql:host=…, from its own config), the code unit talks to one SQLite file
 * per vector that holds the generated package's schema and the vector's seed rows. It is still a PDO (instanceof PDO,
 * every PDO:: constant), so the code runs unchanged. Without a seed it refuses to connect, as before.
 * MySQL functions SQLite lacks are registered (NOW, UNIX_TIMESTAMP, CONCAT, …) so ordinary queries run.
 */

if (class_exists('Pdo\\Sqlite')) {
    class AstraSeededPdoBase extends \Pdo\Sqlite {}
} else {
    class AstraSeededPdoBase extends \PDO {}
}

final class AstraSeededPdo extends AstraSeededPdoBase
{
    /** The SQLite file of the vector being run; null when the run has no database. */
    public static ?string $file = null;
    /** How many connections the code unit opened (for the result notes). */
    public static int $opened = 0;

    public function __construct(...$args)
    {
        if (self::$file === null)
            throw new \PDOException('astra-equivalence: this run has no database (no seed for this vector)');
        parent::__construct('sqlite:' . self::$file);
        self::$opened++;
        $this->setAttribute(\PDO::ATTR_ERRMODE, \PDO::ERRMODE_EXCEPTION);
        // the application's own options (fetch mode, emulate prepares …) still apply
        $options = $args[3] ?? null;
        if (is_array($options)) {
            foreach ($options as $k => $v) {
                if (!is_int($k) || $k === \PDO::ATTR_PERSISTENT) continue;
                try { $this->setAttribute($k, $v); } catch (\Throwable) { /* a MySQL-only attribute */ }
            }
        }
        self::mysqlFunctions($this);
    }

    public static function mysqlFunctions(\PDO $pdo): void
    {
        $reg = function (string $name, callable $fn, int $argc = -1) use ($pdo): void {
            try {
                if (method_exists($pdo, 'createFunction')) $pdo->createFunction($name, $fn, $argc);
                elseif (method_exists($pdo, 'sqliteCreateFunction')) $pdo->sqliteCreateFunction($name, $fn, $argc);
            } catch (\Throwable) { /* a name SQLite keeps for itself */ }
        };
        $reg('NOW', fn() => gmdate('Y-m-d H:i:s'), 0);
        $reg('CURRENT_TIMESTAMP', fn() => gmdate('Y-m-d H:i:s'), 0);
        $reg('CURDATE', fn() => gmdate('Y-m-d'), 0);
        $reg('UNIX_TIMESTAMP', fn($d = null) => $d === null ? time() : (int) strtotime((string) $d));
        $reg('FROM_UNIXTIME', fn($t) => gmdate('Y-m-d H:i:s', (int) $t), 1);
        $reg('CONCAT', fn(...$a) => in_array(null, $a, true) ? null : implode('', array_map('strval', $a)));
        $reg('CONCAT_WS', fn($sep, ...$a) => implode((string) $sep, array_map('strval', array_filter($a, fn($x) => $x !== null))));
        $reg('LCASE', fn($s) => $s === null ? null : strtolower((string) $s), 1);
        $reg('UCASE', fn($s) => $s === null ? null : strtoupper((string) $s), 1);
        $reg('MD5', fn($s) => $s === null ? null : md5((string) $s), 1);
        $reg('SHA1', fn($s) => $s === null ? null : sha1((string) $s), 1);
        $reg('IF', fn($c, $a, $b) => $c ? $a : $b, 3);
        $reg('RAND', fn() => mt_rand() / mt_getrandmax(), 0);
        $reg('LEFT', fn($s, $n) => $s === null ? null : substr((string) $s, 0, (int) $n), 2);
        $reg('RIGHT', fn($s, $n) => $s === null ? null : ((int) $n === 0 ? '' : substr((string) $s, -(int) $n)), 2);
    }

    private static function open(string $file): \PDO
    {
        $pdo = method_exists(\PDO::class, 'connect') ? \PDO::connect('sqlite:' . $file) : new \PDO('sqlite:' . $file);
        $pdo->setAttribute(\PDO::ATTR_ERRMODE, \PDO::ERRMODE_EXCEPTION);
        return $pdo;
    }

    /** A fresh SQLite file for one vector: the schema, then the seed rows. Returns the statements that failed. */
    public static function prepareFile(string $file, array $schema, array $seed): array
    {
        if (is_file($file)) @unlink($file);
        $pdo = self::open($file);
        self::mysqlFunctions($pdo);
        $errors = [];
        foreach ($schema as $stmt) {
            if (!is_string($stmt) || trim($stmt) === '') continue;
            try { $pdo->exec($stmt); } catch (\Throwable $e) { $errors[] = 'schema: ' . $e->getMessage() . ' — ' . substr($stmt, 0, 120); }
        }
        foreach ($seed as $table => $rows) {
            if (!is_string($table) || !preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $table) || !is_array($rows)) continue;
            foreach ($rows as $row) {
                if (!is_array($row) || $row === []) continue;
                $cols = array_keys($row);
                if (array_filter($cols, fn($c) => !is_string($c) || !preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $c))) continue;
                $sql = 'INSERT INTO "' . $table . '" (' . implode(', ', array_map(fn($c) => '"' . $c . '"', $cols)) . ') VALUES ('
                    . implode(', ', array_fill(0, count($cols), '?')) . ')';
                try {
                    $st = $pdo->prepare($sql);
                    $st->execute(array_map(fn($v) => is_bool($v) ? (int) $v : (is_array($v) || is_object($v) ? json_encode($v) : $v), array_values($row)));
                } catch (\Throwable $e) { $errors[] = "seed $table: " . $e->getMessage(); }
            }
        }
        return $errors;
    }

    /** Every row of the named tables after the call, columns lower-cased. */
    public static function dump(string $file, array $tables): array
    {
        $out = [];
        try {
            $pdo = self::open($file);
        } catch (\Throwable $e) {
            return ['__error' => $e->getMessage()];
        }
        foreach ($tables as $t) {
            if (!is_string($t) || !preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $t)) continue;
            try {
                $rows = $pdo->query('SELECT * FROM "' . $t . '"')->fetchAll(\PDO::FETCH_ASSOC);
                $out[strtolower($t)] = array_map(fn($r) => array_change_key_case($r, CASE_LOWER), $rows);
            } catch (\Throwable $e) {
                $out[strtolower($t)] = ['__error' => $e->getMessage()];
            }
        }
        return $out;
    }
}
