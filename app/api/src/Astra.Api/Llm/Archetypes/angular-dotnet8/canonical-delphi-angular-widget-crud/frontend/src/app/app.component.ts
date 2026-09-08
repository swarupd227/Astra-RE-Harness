import { Component } from '@angular/core';
import { WidgetListComponent } from './widget-list/widget-list.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [WidgetListComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent {
  title = 'Widget Manager';
}
