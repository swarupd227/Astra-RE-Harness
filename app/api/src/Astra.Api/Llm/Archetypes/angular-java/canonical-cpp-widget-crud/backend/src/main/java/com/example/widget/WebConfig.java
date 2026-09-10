package com.example.widget;

import org.springframework.context.annotation.Configuration;
import org.springframework.web.servlet.config.annotation.CorsRegistry;
import org.springframework.web.servlet.config.annotation.WebMvcConfigurer;

/**
 * Permissive dev CORS policy for the Angular dev server (ng serve on
 * :4200) — a real deployment would replace this with the actual allowed
 * origin(s) for its hosting environment, same caveat as
 * angular-dotnet8/canonical-delphi-angular-widget-crud's AngularDevCors
 * policy in Program.cs.
 */
@Configuration
public class WebConfig implements WebMvcConfigurer {
    @Override
    public void addCorsMappings(CorsRegistry registry) {
        registry.addMapping("/api/**")
            .allowedOrigins("http://localhost:4200")
            .allowedMethods("GET", "POST", "DELETE");
    }
}
