// SPDX-Spec: java/SecurityConfig.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

/**
 * SB-1 / JAK-1: documented contract for the Spring Boot 2 → 3 security upgrade.
 * The Java-11 source extended {@code WebSecurityConfigurerAdapter} and used
 * {@code antMatchers}; Spring Boot 3 (Spring Security 6) REMOVED the adapter, so
 * the config becomes a {@code SecurityFilterChain} @Bean using
 * {@code requestMatchers}, and every {@code javax.servlet.*} import moves to
 * {@code jakarta.servlet.*}.
 *
 * <p>This can't be compiled offline (no Spring/jakarta in the maven-sidecar
 * cache), so the mapping lives here as a documented contract + the access-control
 * INVARIANT the rewrite must preserve — NOT stubbed live wiring. On promotion,
 * replace this class with the real {@code @Configuration} bean shown in
 * {@link #TARGET_SHAPE}.
 */
@SpecClaim("SB-1")
@SpecClaim("JAK-1")
@Modernization(value = "SecurityFilterChain @Bean + requestMatchers (jakarta.servlet)",
               from = "WebSecurityConfigurerAdapter + antMatchers (javax.servlet)")
public final class SecurityConfigContract {

    /** The invariant the Spring Boot 3 rewrite MUST preserve exactly (INV-1). */
    public static final String INVARIANT =
        "/admin/** requires role ADMIN; /public/** is permitAll; every other request is authenticated";

    /** The target Spring Boot 3 shape (documentation; not compiled — no Spring offline). */
    public static final String TARGET_SHAPE = """
        @Bean
        SecurityFilterChain filterChain(HttpSecurity http) throws Exception {
            return http
                .authorizeHttpRequests(auth -> auth
                    .requestMatchers("/admin/**").hasRole("ADMIN")
                    .requestMatchers("/public/**").permitAll()
                    .anyRequest().authenticated())
                .build();
        }
        """;

    private SecurityConfigContract() {
    }
}
