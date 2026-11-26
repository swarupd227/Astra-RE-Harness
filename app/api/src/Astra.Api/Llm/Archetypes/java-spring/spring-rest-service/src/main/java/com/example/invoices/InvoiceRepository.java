package com.example.invoices;

import java.math.BigDecimal;
import java.sql.PreparedStatement;
import java.sql.Statement;
import java.util.Objects;
import java.util.Optional;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.jdbc.support.GeneratedKeyHolder;
import org.springframework.jdbc.support.KeyHolder;
import org.springframework.stereotype.Repository;

/**
 * Every statement the legacy routine ran, one method each, with its values BOUND as parameters — never
 * concatenated into the SQL text, even where the source concatenated them.
 */
@Repository
@SpecClaim("DA-1")
@SpecClaim("DA-2")
public class InvoiceRepository {

    private static final RowMapper<Invoice> ROW = (rs, n) ->
            new Invoice(rs.getLong("id"), rs.getLong("customer_id"), rs.getBigDecimal("amount"), rs.getString("note"));

    private final JdbcTemplate jdbc;

    public InvoiceRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public Optional<Invoice> findById(long id) {
        return jdbc.query("SELECT id, customer_id, amount, note FROM invoices WHERE id = ?", ROW, id)
                .stream().findFirst();
    }

    public boolean customerExists(long customerId) {
        Integer n = jdbc.queryForObject("SELECT COUNT(*) FROM customers WHERE id = ?", Integer.class, customerId);
        return n != null && n > 0;
    }

    public long insert(long customerId, BigDecimal amount, String note) {
        KeyHolder keys = new GeneratedKeyHolder();
        jdbc.update(con -> {
            PreparedStatement ps = con.prepareStatement(
                    "INSERT INTO invoices (customer_id, amount, note) VALUES (?, ?, ?)", Statement.RETURN_GENERATED_KEYS);
            ps.setLong(1, customerId);
            ps.setBigDecimal(2, amount);
            ps.setString(3, note);
            return ps;
        }, keys);
        return Objects.requireNonNull(keys.getKey(), "the insert returned no id").longValue();
    }
}
