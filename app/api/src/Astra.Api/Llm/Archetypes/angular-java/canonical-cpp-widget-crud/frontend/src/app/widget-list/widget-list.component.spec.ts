import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Widget } from '../models/widget';
import { WidgetListComponent } from './widget-list.component';

describe('WidgetListComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WidgetListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('loads and renders widgets on init', () => {
    const widgets: Widget[] = [{ id: 1, name: 'Bolt', quantity: 10 }];
    const fixture = TestBed.createComponent(WidgetListComponent);

    fixture.detectChanges();
    httpMock.expectOne('/api/widgets').flush(widgets);
    fixture.detectChanges();

    const rows = (fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('Bolt');
  });

  it('surfaces an error message when loading fails', () => {
    const fixture = TestBed.createComponent(WidgetListComponent);

    fixture.detectChanges();
    httpMock.expectOne('/api/widgets').error(new ProgressEvent('network error'));
    fixture.detectChanges();

    expect(fixture.componentInstance.errorMessage).toContain('Could not load widgets');
  });
});
