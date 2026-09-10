package com.example.warmup;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * Throw-away Spring Boot app used ONLY so the image-build-time {@code mvn
 * test} below exercises a real embedded-MVC test slice (see WarmupTest) —
 * that's what forces Maven to resolve and cache the full spring-boot-starter-
 * web / -test dependency tree (Tomcat embed, Jackson, spring-test, hamcrest,
 * JSONassert, jsonpath, ...) into the baked ~/.m2, not just this project's
 * own two jars. Deleted along with the rest of /warmup after the build.
 */
@SpringBootApplication
public class WarmupApplication {

    @RestController
    static class PingController {
        @GetMapping("/ping")
        String ping() {
            return "pong";
        }
    }

    public static void main(String[] args) {
        SpringApplication.run(WarmupApplication.class, args);
    }
}
