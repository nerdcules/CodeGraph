package org.acme.shop;

/** Prices an order line. */
public interface Pricing {
    long price(String sku, int quantity);
}
