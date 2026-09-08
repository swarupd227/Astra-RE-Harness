import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Widget } from './models/widget';
import { WidgetService } from './widget.service';

describe('WidgetService', () => {
  let service: WidgetService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WidgetService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('lists widgets from GET /api/widgets', () => {
    const expected: Widget[] = [{ id: 1, name: 'Bolt', quantity: 10 }];

    service.list().subscribe((widgets) => expect(widgets).toEqual(expected));

    const req = httpMock.expectOne('/api/widgets');
    expect(req.request.method).toBe('GET');
    req.flush(expected);
  });

  it('creates a widget via POST /api/widgets', () => {
    const created: Widget = { id: 2, name: 'Nut', quantity: 5 };

    service.create({ name: 'Nut', quantity: 5 }).subscribe((widget) => expect(widget).toEqual(created));

    const req = httpMock.expectOne('/api/widgets');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Nut', quantity: 5 });
    req.flush(created);
  });

  it('deletes a widget via DELETE /api/widgets/:id', () => {
    service.remove(2).subscribe();

    const req = httpMock.expectOne('/api/widgets/2');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });
});
