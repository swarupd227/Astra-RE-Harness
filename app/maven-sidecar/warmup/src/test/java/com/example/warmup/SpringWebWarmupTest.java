package com.example.warmup;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.web.servlet.MockMvc;

/**
 * A trivial Spring MVC test that exists ONLY so the image-build-time
 * `mvn test` forces Maven to resolve and cache the full spring-boot-
 * starter-web / -test dependency tree (embedded Tomcat, Jackson,
 * spring-test's MockMvc, hamcrest, jsonpath, ...) plus the
 * spring-boot-starter-parent POM chain — none of which a plain
 * `dependency:resolve` reliably discovers, same rationale as WarmupTest
 * for the JUnit Platform provider. Without this, angular-java scaffolds'
 * `mvn -o test` fails offline the first time a real controller test runs.
 */
@SpringBootTest(classes = WarmupApplication.class)
@AutoConfigureMockMvc
class SpringWebWarmupTest {

    @Autowired
    private MockMvc mockMvc;

    @Test
    void pingEndpointRespondsOverMockMvc() throws Exception {
        mockMvc.perform(get("/ping"))
            .andExpect(status().isOk())
            .andExpect(content().string("pong"));
    }
}
