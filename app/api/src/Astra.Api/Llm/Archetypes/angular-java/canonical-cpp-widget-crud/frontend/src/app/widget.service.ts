// SPDX-Spec: cpp (signed)
// SPDX-Archetype: canonical-cpp-widget-crud
//
// TypeScript has no attribute mechanism as lightweight as Java's
// @SpecClaim, so claim citations here are a `@SpecClaim ID` line in the
// doc comment directly above the cited declaration — same intent (map
// generated code back to the signed spec), same id vocabulary, just a
// comment instead of an annotation.

import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { NewWidget, Widget } from './models/widget';

/**
 * Talks to the Spring Boot backend that ships alongside this Angular
 * app. Kept deliberately thin — one method per REST verb, no client-side
 * caching or state management — so a migrated routine's real behavior
 * lives in the backend service it calls, not duplicated here.
 *
 * @SpecClaim INV-1
 */
@Injectable({ providedIn: 'root' })
export class WidgetService {
  private readonly baseUrl = '/api/widgets';

  constructor(private readonly http: HttpClient) {}

  list(): Observable<Widget[]> {
    return this.http.get<Widget[]>(this.baseUrl);
  }

  create(widget: NewWidget): Observable<Widget> {
    return this.http.post<Widget>(this.baseUrl, widget);
  }

  remove(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
